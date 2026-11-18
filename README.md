# QuasarBridge (C#)

MITM / transparent proxy для `wss://uniproxy.alice.yandex.net/uni.ws` на этом ПК.

## Настройка с нуля

Нужны уже рабочие **root + SSH** на станции. Дальше — DNS, сертификат, firewall, запуск и чеклист:

→ **[`docs/setup-from-scratch.md`](docs/setup-from-scratch.md)**

## Схема

1. AdGuard / OpenWrt DNS rewrite: `uniproxy*.alice.yandex.net → 192.168.0.176`
2. QuasarBridge слушает `https://192.168.0.176:443` с SAN на этот хост
3. Станция думает, что ходит в Яндекс; bridge проксирует в настоящий upstream (DoH)
4. Фразы «продолжить/включить сериал» и то же про фильм читаются из распознавания Яндекса. Мост ищет запись в Jellyfin и открывает её в PotPlayer. «Включить» без названия спрашивает, какой именно; ответ «последний» продолжает последний просмотр. «Выключи сериал/фильм» или «закрой плеер» пишет текущую секунду в Jellyfin и закрывает PotPlayer. Ключ Jellyfin лежит в `appsettings.Local.json`.

## Запуск

```powershell
cd C:\Cursor AI\Projects\QuasarBridge
.\scripts\start.ps1
```

```powershell
Invoke-RestMethod http://127.0.0.1:8443/healthz
curl.exe -sk --resolve uniproxy.alice.yandex.net:443:192.168.0.176 https://uniproxy.alice.yandex.net/healthz
```

API: `/healthz`, `/api/status`, `/api/rules`, `/api/events`, `/swagger`

Медиа-команды (фраза → decode → ответ станции): [`docs/media-pipeline.md`](docs/media-pipeline.md)

## Сертификаты

Каталог `certs/` (fullchain + key / pfx). Thumbprint в `appsettings.json`.

Своя CA на ~100 лет, генерация + установка на ПК и станцию:

→ **[`docs/mitm-certificate-100y.md`](docs/mitm-certificate-100y.md)** · `scripts/Generate-QuasarMitmCert.ps1` · `scripts/Install-QuasarMitmCert.ps1`

## DNS

Кратко: `scripts/adguard-dns-rewrite.md`. Полный порядок — в `docs/setup-from-scratch.md`.

## Ориентиры

- [qprots/station-max](https://github.com/qprots/station-max)
- [yniyniyni/librestation](https://github.com/yniyniyni/librestation)
