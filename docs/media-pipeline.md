# Медиа-команды: три шага

Поток «включи сериал» → Jellyfin → PotPlayer разделён на три явных этапа.

```
ASR (station→upstream)  →  CapturePhrase  →  DecodeAsync  →  MediaStationDelivery.Plan  →  WebSocket на станцию
```

| Шаг | Метод | Файл | Что делает |
|-----|--------|------|------------|
| **1. Фраза** | `MediaSession.CapturePhrase(PhraseCaptureOptions)` | `Services/Media/MediaSession.cs` | Собирает гипотезу из токенов ASR (`AsrText.JoinContent`), флаг `NeedsModel`, pending-уточнение. |
| **2. Расшифровка** | `MediaSession.DecodeAsync(MediaPhrase, ct)` | там же | `MediaIntents` / FreeLLM → Jellyfin / PotPlayer → `MediaTurn` (play, silence, passthrough). |
| **3. Ответ станции** | `MediaStationDelivery.Plan(MediaTurn, frame)` | `Services/Media/MediaStationDelivery.cs` | Заглушка текста Алисы, длительность mute, payload для WebSocket. |

## Где вызывается

- **`UniProxyService`**: при кадре `IsReply` — `DecideAsync` (= шаг 1+2), затем `MediaStationDelivery.Plan` (= шаг 3) и `SendAsync`.
- **Ранний запуск** (без ответа облака): `TryActOnStableAsrAsync` → `CapturePhrase` + `DecodeAsync`.

## Типы

- `MediaPhrase` — снимок очереди токенов на один «ход».
- `StationDeliveryPlan` — `Mode`, `Payload`, `MuteFor`, `Note`.

Поиск в Jellyfin: `MediaIntents.CompactSearchTitle` отрезает синопсис/UI с экрана Яндекса, оставляет хвост («рик морти»).