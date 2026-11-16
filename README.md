# QuasarBridge (C#)

MITM / transparent proxy для `wss://uniproxy.alice.yandex.net/uni.ws` на этом ПК.

## Настройка с нуля

Нужны уже рабочие **root + SSH** на станции. Дальше — DNS, сертификат, firewall, запуск и чеклист:

→ **[`docs/setup-from-scratch.md`](docs/setup-from-scratch.md)**

## Схема

1. AdGuard / OpenWrt DNS rewrite: `uniproxy*.alice.yandex.net → 192.168.0.176`
2. QuasarBridge слушает `https://192.168.0.176:443` с SAN на этот хост
3. Станция думает, что ходит в Яндекс; bridge проксирует в настоящий upstream (DoH)
4. В `hybrid` правила из SQLite могут локально перехватить ответ (пример: сериал → PotPlayer, TTS режется)

## Запуск

```powershell
cd C:\Users\Admin\Documents\Projects\QuasarBridge
.\scripts\start.ps1
```

```powershell
Invoke-RestMethod http://127.0.0.1:8443/healthz
curl.exe -sk --resolve uniproxy.alice.yandex.net:443:192.168.0.176 https://uniproxy.alice.yandex.net/healthz
```

API: `/healthz`, `/api/status`, `/api/rules`, `/api/events`, `/swagger`

## Сертификаты

Каталог `certs/` (fullchain + key / pfx). Thumbprint в `appsettings.json`.

## DNS

Кратко: `scripts/adguard-dns-rewrite.md`. Полный порядок — в `docs/setup-from-scratch.md`.

## Ориентиры

- [qprots/station-max](https://github.com/qprots/station-max)
- [yniyniyni/librestation](https://github.com/yniyniyni/librestation)
