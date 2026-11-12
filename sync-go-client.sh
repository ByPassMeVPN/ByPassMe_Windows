#!/usr/bin/env bash
# 12.11.2026: копия go_client для Windows РАЗОШЛАСЬ с Android и полной
# синхронизации больше не подлежит: rawtun_windows.go/rawtun_other.go,
# protectHostRoute в session.go, флаги -tun-name/-exclude-ips в main.go.
# Прежний вариант (rsync --delete из Android) удалял бы этот код.
# Общие правки переносить точечно (общие файлы: group.go, liveness.go,
# dispatcher.go и т.д. — сверять diff перед копированием).
echo "Полная синхронизация go_client отключена: копии разошлись. Переносите правки точечно (см. комментарий в скрипте)." >&2
exit 1
