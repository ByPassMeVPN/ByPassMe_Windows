//go:build !windows

package main

// На других платформах туннель поднимает само приложение (Android VpnService,
// iOS Network Extension), и соединения клиента исключаются по приложению.
func protectHostRoute(string) {}
