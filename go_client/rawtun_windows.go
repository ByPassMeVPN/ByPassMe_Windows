//go:build windows

package main

import (
	"fmt"
	"log"
	"net"
	"os/exec"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"golang.zx2c4.com/wireguard/tun"
)

// Обход на Windows без WireGuard: адаптер wintun, в который диспетчер пишет
// IP-пакеты напрямую (режим rawtun, сервер -listen-raw :56003). Адрес, DNS и
// маршруты выставляются через netsh — так же, как это делает PWDTT.

// Подсети VK и локальной сети всегда идут мимо туннеля: иначе TURN-трафик
// самого клиента зациклится в туннель и связь оборвётся.
var rawTunExcludeCIDRs = []string{
	"95.163.0.0/16",
	"87.240.0.0/16",
	"93.186.224.0/20",
	"185.32.248.0/22",
	"185.29.130.0/24",
	"217.20.144.0/20",
	"10.0.0.0/8",
	"172.16.0.0/12",
	"192.168.0.0/16",
	"169.254.0.0/16",
}

// activeRawTun — поднятый туннель. Нужен, чтобы соединения самого клиента с
// TURN-серверами VK уводить мимо туннеля: на Windows нет исключения «по
// приложению», как в Android VpnService, и без host-маршрута трафик клиента
// ушёл бы в собственный туннель и всё оборвалось бы.
var activeRawTun atomic.Pointer[rawTunDevice]

// protectHostRoute добавляет маршрут до host мимо туннеля (вызывается перед
// подключением к TURN-relay).
func protectHostRoute(hostPort string) {
	t := activeRawTun.Load()
	if t == nil || t.gateway == "" {
		return
	}
	host := hostPort
	if h, _, err := net.SplitHostPort(hostPort); err == nil {
		host = h
	}
	ips := []net.IP{}
	if ip := net.ParseIP(host); ip != nil {
		ips = append(ips, ip)
	} else {
		resolved, err := net.LookupIP(host)
		if err != nil {
			return
		}
		ips = resolved
	}
	for _, ip := range ips {
		v4 := ip.To4()
		if v4 == nil {
			continue
		}
		t.addHostRoute(v4.String())
	}
}

type rawTunDevice struct {
	dev     tun.Device
	name    string
	gateway string
	routes  []string // маршруты мимо туннеля, снимаются при остановке

	rmu     sync.Mutex
	routeMu sync.Mutex
	rbufs   [][]byte
	sizes   []int
	queue   [][]byte // пакеты, прочитанные пачкой, но ещё не отданные

	wmu   sync.Mutex
	wbuf  [][]byte
	close sync.Once
}

// startRawTun поднимает адаптер, выставляет адрес и маршруты и возвращает
// устройство для диспетчера.
func startRawTun(name, addrCIDR, dnsCSV string, mtu int, excludes []string) (*rawTunDevice, error) {
	ip, mask := parseAddrMask(addrCIDR)
	if ip == "" {
		return nil, fmt.Errorf("некорректный адрес туннеля %q", addrCIDR)
	}

	dev, err := tun.CreateTUN(name, mtu)
	if err != nil {
		return nil, fmt.Errorf("создание адаптера: %w", err)
	}
	if realName, nameErr := dev.Name(); nameErr == nil && realName != "" {
		name = realName
	}
	t := &rawTunDevice{
		dev:   dev,
		name:  name,
		rbufs: make([][]byte, tunBatchSize),
		sizes: make([]int, tunBatchSize),
		wbuf:  make([][]byte, 1),
	}
	for i := range t.rbufs {
		t.rbufs[i] = make([]byte, tunReadBuf)
	}

	// Адрес на адаптере. Метрика 1 — чтобы туннель выигрывал у физической сети.
	if err := runNetsh("interface", "ip", "set", "address", "name="+name, "source=static", ip, mask); err != nil {
		dev.Close()
		return nil, fmt.Errorf("адрес %s: %w", ip, err)
	}
	_ = runNetsh("interface", "ip", "set", "interface", "interface="+name, "metric=1")

	first := true
	for _, d := range splitCSV(dnsCSV) {
		if net.ParseIP(d) == nil {
			continue
		}
		if first {
			_ = runNetsh("interface", "ip", "set", "dns", "name="+name, "source=static", "addr="+d, "register=none", "validate=no")
			first = false
		} else {
			_ = runNetsh("interface", "ip", "add", "dns", "name="+name, "addr="+d, "index=2", "validate=no")
		}
	}

	// Весь трафик в туннель: две половины /1 перебивают маршрут по умолчанию
	// физической сети, не удаляя его.
	for _, r := range []string{"0.0.0.0/1", "128.0.0.0/1"} {
		if err := runNetsh("interface", "ip", "add", "route", r, name, "metric=1", "store=active"); err != nil {
			log.Printf("[RAW] Маршрут %s: %v", r, err)
		}
	}

	// Мимо туннеля — через физический шлюз. Маршрутов сотни (домены
	// «напрямую»), поэтому добавляем их параллельно: по одному это занимало
	// около трёх секунд, и туннель поднимался заметно позже воркеров.
	t.gateway = defaultGateway()
	if t.gateway == "" {
		log.Println("[RAW] Шлюз сети не найден — исключения из туннеля не добавлены")
	} else {
		started := time.Now()
		var wg sync.WaitGroup
		var addedMu sync.Mutex
		added := make([]string, 0, len(excludes)+len(rawTunExcludeCIDRs))
		sem := make(chan struct{}, 24)
		seen := map[string]bool{}
		for _, cidr := range append(append([]string{}, rawTunExcludeCIDRs...), excludes...) {
			cidr = strings.TrimSpace(cidr)
			if cidr == "" {
				continue
			}
			if !strings.Contains(cidr, "/") {
				if net.ParseIP(cidr) == nil {
					continue
				}
				cidr += "/32"
			}
			dst, m := parseAddrMask(cidr)
			if dst == "" || seen[dst] {
				continue
			}
			seen[dst] = true
			wg.Add(1)
			go func(dst, m string) {
				defer wg.Done()
				sem <- struct{}{}
				defer func() { <-sem }()
				if err := runCmd("route", "add", dst, "mask", m, t.gateway, "metric", "1"); err == nil {
					addedMu.Lock()
					added = append(added, dst)
					addedMu.Unlock()
				}
			}(dst, m)
		}
		wg.Wait()
		t.routes = added
		log.Printf("[RAW] Мимо туннеля: %d адресов за %s (шлюз %s)",
			len(t.routes), time.Since(started).Round(time.Millisecond), t.gateway)
	}

	activeRawTun.Store(t)
	log.Printf("[RAW] Адаптер %s поднят: %s, MTU %d", name, addrCIDR, mtu)
	return t, nil
}

const (
	tunBatchSize = 32
	tunReadBuf   = 2048
)

func (t *rawTunDevice) Read(p []byte) (int, error) {
	t.rmu.Lock()
	defer t.rmu.Unlock()
	for {
		if len(t.queue) > 0 {
			pkt := t.queue[0]
			t.queue = t.queue[1:]
			return copy(p, pkt), nil
		}
		n, err := t.dev.Read(t.rbufs, t.sizes, 0)
		if err != nil {
			return 0, err
		}
		for i := 0; i < n; i++ {
			if t.sizes[i] <= 0 {
				continue
			}
			pkt := make([]byte, t.sizes[i])
			copy(pkt, t.rbufs[i][:t.sizes[i]])
			t.queue = append(t.queue, pkt)
		}
	}
}

func (t *rawTunDevice) Write(p []byte) (int, error) {
	if len(p) == 0 {
		return 0, nil
	}
	t.wmu.Lock()
	defer t.wmu.Unlock()
	t.wbuf[0] = p
	if _, err := t.dev.Write(t.wbuf, 0); err != nil {
		return 0, err
	}
	return len(p), nil
}

// Fd для Windows смысла не имеет — диспетчеру он нужен только в логе.
func (t *rawTunDevice) Fd() uintptr { return 0 }

// addHostRoute уводит один адрес мимо туннеля (повторные вызовы игнорируются).
func (t *rawTunDevice) addHostRoute(ip string) {
	t.routeMu.Lock()
	defer t.routeMu.Unlock()
	for _, existing := range t.routes {
		if existing == ip {
			return
		}
	}
	if err := runCmd("route", "add", ip, "mask", "255.255.255.255", t.gateway, "metric", "1"); err == nil {
		t.routes = append(t.routes, ip)
		log.Printf("[RAW] Мимо туннеля: %s", ip)
	}
}

func (t *rawTunDevice) Close() error {
	var err error
	t.close.Do(func() {
		activeRawTun.Store(nil)
		t.routeMu.Lock()
		defer t.routeMu.Unlock()
		// Снимаем маршруты тоже параллельно — иначе остановка тянется секунды.
		var wg sync.WaitGroup
		sem := make(chan struct{}, 24)
		for _, dst := range t.routes {
			wg.Add(1)
			go func(dst string) {
				defer wg.Done()
				sem <- struct{}{}
				defer func() { <-sem }()
				_ = runCmd("route", "delete", dst)
			}(dst)
		}
		wg.Wait()
		t.routes = nil
		err = t.dev.Close()
		log.Printf("[RAW] Адаптер %s остановлен", t.name)
	})
	return err
}

// defaultGateway — шлюз физической сети (первый маршрут 0.0.0.0/0 не через наш адаптер).
func defaultGateway() string {
	out, err := runCmdOut("route", "print", "-4", "0.0.0.0")
	if err != nil {
		return ""
	}
	for _, line := range strings.Split(out, "\n") {
		f := strings.Fields(line)
		if len(f) >= 4 && f[0] == "0.0.0.0" && f[1] == "0.0.0.0" {
			if ip := net.ParseIP(f[2]); ip != nil && ip.To4() != nil && !ip.IsUnspecified() {
				return f[2]
			}
		}
	}
	return ""
}

func parseAddrMask(cidr string) (string, string) {
	cidr = strings.TrimSpace(cidr)
	if !strings.Contains(cidr, "/") {
		if ip := net.ParseIP(cidr); ip != nil && ip.To4() != nil {
			return ip.String(), "255.255.255.255"
		}
		return "", ""
	}
	ip, ipnet, err := net.ParseCIDR(cidr)
	if err != nil || ip.To4() == nil {
		return "", ""
	}
	m := ipnet.Mask
	if len(m) != 4 {
		return "", ""
	}
	return ip.String(), fmt.Sprintf("%d.%d.%d.%d", m[0], m[1], m[2], m[3])
}

func runNetsh(args ...string) error { return runCmd("netsh", args...) }

func runCmd(name string, args ...string) error {
	_, err := runCmdOut(name, args...)
	return err
}

func runCmdOut(name string, args ...string) (string, error) {
	cmd := exec.Command(name, args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	done := make(chan struct{})
	var out []byte
	var err error
	go func() {
		out, err = cmd.CombinedOutput()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(15 * time.Second):
		_ = cmd.Process.Kill()
		return "", fmt.Errorf("%s: таймаут", name)
	}
	if err != nil {
		return string(out), fmt.Errorf("%s %s: %v (%s)", name, strings.Join(args, " "), err, strings.TrimSpace(string(out)))
	}
	return string(out), nil
}

func splitCSV(s string) []string {
	var out []string
	for _, part := range strings.Split(s, ",") {
		if p := strings.TrimSpace(part); p != "" {
			out = append(out, p)
		}
	}
	return out
}
