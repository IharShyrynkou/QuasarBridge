# QuasarBridge (C#)

MITM / transparent proxy для `wss://uniproxy.alice.yandex.net/uni.ws` на этом ПК.

## Схема

1. AdGuard / OpenWrt DNS rewrite: `uniproxy.alice.yandex.net → 192.168.0.176`
2. QuasarBridge слушает `https://192.168.0.176:443` с сертификатом, у которого этот хост в SAN
3. Станция думает, что ходит в Яндекс; bridge проксирует в настоящий upstream
4. Upstream резолвится через **DNS-over-HTTPS**, чтобы не зациклиться на собственном rewrite

## Режимы

| Mode | Поведение |
|---|---|
| `observe` (по умолчанию) | логирует и форвардит всё в Яндекс |
| `hybrid` | правила `Routing` могут пометить кадр как `local` / `drop` |

Полноценный protobuf-перехват голоса (как в Go `station-max/quasar-bridge-patches`) — следующий этап.
Сейчас `local` — заготовка под SmartTube / LLM / HA.

## Запуск

```powershell
cd C:\Users\Admin\Documents\deepseek-harness\default-workspace\QuasarBridge
.\scripts\start.ps1
```

Проверка TLS:

```powershell
curl.exe -sk --resolve uniproxy.alice.yandex.net:443:192.168.0.176 https://uniproxy.alice.yandex.net/healthz
```

HTTP API: `http://127.0.0.1:8443/healthz`, `/api/status`, `/swagger`

## Сертификаты

Файлы: `C:\Users\Admin\Pictures\certs\fullchain.pem` + `quasar-all.key`  
SAN включает `uniproxy.alice.yandex.net` (до 2029-01-22).

На Windows Schannel **нельзя** отдавать ephemeral private key в server TLS.
Сертификат один раз импортируется в `CurrentUser\My` (thumbprint в `appsettings.json` → `TlsStoreThumbprint`).

## DNS

См. `scripts/adguard-dns-rewrite.md`.

## Ориентиры

- [qprots/station-max](https://github.com/qprots/station-max) — Go Quasar Bridge (hybrid routing, SmartTube, LLM)
- [yniyniyni/librestation](https://github.com/yniyniyni/librestation) — полная замена Алисы (Spotify + Gemini на колонке), другой путь
