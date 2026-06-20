# ByPassMe Windows

Windows-версия **ByPassMe** — точная копия Android-приложения (v1.1.7).

## Установка (для пользователя)

1. Скачайте **`ByPassMe-Setup-1.1.7.exe`** из [Releases](https://github.com/Andrei2009020/ByPassMe/releases)
2. Запустите установщик → «Далее» → «Установить»
3. Готово — WireGuard уже внутри, ничего отдельно качать не нужно
4. Запустите **ByPassMe** с рабочего стола или из меню Пуск

При первом **Подключить** Windows спросит права администратора (для VPN-туннеля) — это нормально.

## Что внутри установщика

| Компонент | Описание |
|-----------|----------|
| `ByPassMe.exe` | WPF-приложение (.NET 8 self-contained) |
| `bypassclient.exe` | Go-клиент VK TURN + RJS-капча |
| `tools/wireguard.exe` | WireGuard tunnel manager |
| `tools/wintun.dll` | Драйвер TUN (Wintun) |

Всё ставится в `%LOCALAPPDATA%\ByPassMe\` — без прав администратора при установке.

## Возможности

- Онбординг с ссылкой подписки
- **Обход Б/С** — VK TURN + WireGuard
- **Логи** с фильтрами
- Темы: системная / светлая / тёмная + палитры Indigo / Forest / Espresso
- Планшетное окно 412×892 (без разворота на весь экран)

## Сборка (разработчик)

```powershell
# Токен hub.mos.ru
copy Secrets.props.example Secrets.props

# Установщик (нужен Inno Setup 6)
.\build-windows.ps1 -CreateInstaller
```

Результат: `dist\ByPassMe-Setup-1.1.7.exe`

### GitHub Actions

При push в `main`/`master` CI собирает установщик и публикует Release.

**Secret:** `HUB_MOS_TOKEN` в Settings → Secrets → Actions

### Синхронизация Go-клиента

Источник — [proxy-turn-vk-android/go_client](https://github.com/SpaceNeuroX/proxy-turn-vk-android) (qWDTT), тот же код что в `libclient.so` на Android.

## Капча и транспорт

Go-клиент совпадает с **qWDTT / Android libclient.so**:

- **RJS v2** — автоматическое решение VK Smart Captcha (как на Android)
- **WRAP** — RTP AEAD обфускация из пароля подключения (`-password`)
- **TURN UDP** — как в рабочих логах Android

WebView2 используется только как fallback, если RJS не справится.

## Версия

1.1.7
