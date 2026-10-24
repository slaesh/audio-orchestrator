# Home Assistant Integration: MQTT vs. HACS Integration

Audio Orchestrator bietet zwei verschiedene Möglichkeiten zur Anbindung an [Home Assistant](https://www.home-assistant.io):

1. **Native MQTT Auto-Discovery** (Direkt im Audio Orchestrator integriert)
2. **Custom Component via HACS** ([`slaesh/audio-orchestrator-hass-integration`](https://github.com/slaesh/audio-orchestrator-hass-integration))

Beide Wege haben spezifische Vor- und Nachteile. Diese Übersicht hilft dir bei der Wahl der passenden Methode.

---

## Schneller Vergleich

| Feature / Kriterium | Native MQTT Integration | HACS Custom Integration |
|---|:---:|:---:|
| **Installation** | Keine Installation in HA nötig (nur MQTT Broker) | Installation via HACS & HA Neustart |
| **Konfiguration** | In Orchestrator-UI: Schalter an + Broker-IP | In HA: IP-Adresse des Orchestrators eingeben |
| **Home Assistant Entitätstyp** | Geräte mit `select`, `number`, `switch`, `sensor`, `button` | Echter `media_player.<gruppe>` |
| **Native HA Medien-Ansicht (`/home/media-players`)** | ❌ Nein (HA Core unterstützt kein MQTT `media_player`) |  Ja, voll integriert |
| **Mini Media Player Card (Lovelace)** | ❌ Nur über Universal Media Player Wrapper |  Direkt out-of-the-box kompatibel |
| **Automationen & Sprachassistenten** |  Hervorragend (Drehregler, Schalter, Szenen) |  Hervorragend (`media_player.*` Services) |
| **Transport-Buttons (Play / Pause / Next / Prev / Stop)** |  Dynamisch (nur bei aktiver Spotify-Quelle aktiv) |  Dynamisch je nach Stream-Typ |
| **Quellen-Auswahl (Radio & Spotify)** |  Dropdown-Entity (`select`) |  Source-List im Media Player |
| **Metadaten (Song, Artist, Album, Playlist, Cover)** |  Über Sensor-Attribute & Text-Sensor |  Direkt in Media Player Attributen |
| **Übertragungsart** | Push über MQTT Broker (Echtzeit) | Polling (3s Intervall via REST API) |

---

## 1. Native MQTT Auto-Discovery

### Wann ist dieser Weg ideal?
- Du hast bereits einen MQTT-Broker (z. B. Mosquitto Add-on in Home Assistant) laufen.
- Du möchtest **keine Custom Integration** oder HACS in Home Assistant installieren.
- Du willst deine Audio-Gruppen primär in **Automationen**, mit **physischen Schaltern / Drehknöpfen** (z. B. Zigbee / Z-Wave) steuern oder dir eigene Dashboards mit Standard-Cards bauen.

### Wie es funktioniert:
1. Öffne im Audio Orchestrator Web-Interface die **Einstellungen** (Zahnrad-Symbol).
2. Aktiviere **"Home Assistant MQTT-Integration aktivieren"**.
3. Gib deine Broker-IP (z. B. `192.168.178.10` oder `homeassistant`), Port (1883) und optionale Zugangsdaten ein.
4. Klicke auf **"Verbindung testen"** und danach auf **"Speichern"**.

Jede Gruppe taucht sofort als eigenes **Home Assistant Gerät** unter *Einstellungen -> Geräte & Dienste -> MQTT* auf:
- `select.<gruppe>_quelle`: Dropdown aller Radiosender und Spotify-Nodes sowie `"Aus (Stille)"`.
- `number.<gruppe>_lautstarke`: Slider für Master-Lautstärke (0–100%).
- `switch.<gruppe>_stummschaltung`: Mute-Schalter.
- `sensor.<gruppe>_status`: Playback-Status (`playing`, `paused`, `idle`) mit allen Attributen (Song, Artist, Cover-URL, Raumnamen).
- `sensor.<gruppe>_titel`: Text-Sensor mit aktuellem Titel/Sender.
- `button.<gruppe>_wiedergabe`, `pause`, `stopp`, `nachster_titel`, `vorheriger_titel`: Transport-Buttons (werden automatisch als `unavailable` ausgeblendet, wenn kein Spotify-Stream aktiv ist).

---

## 2. HACS Custom Integration

### Wann ist dieser Weg ideal?
- Du möchtest die offizielle Home Assistant **Medien-Übersicht (`/home/media-players`)** nutzen.
- Du verwendest beliebte Lovelace-Erweiterungen wie die **`mini-media-player`**-Karte.
- Du möchtest Audio Orchestrator über Apples HomeKit oder Googles Media-Steuerung als Lautsprecher weiterleiten.

### Wie es funktioniert:
1. Öffne **HACS** in Home Assistant $\rightarrow$ Drei Punkte oben rechts $\rightarrow$ *Benutzerdefinierte Repositories*.
2. URL: `https://github.com/slaesh/audio-orchestrator-hass-integration`, Kategorie: *Integration*.
3. Herunterladen, Home Assistant neu starten.
4. Unter *Einstellungen -> Geräte & Dienste* nach **"Audio Orchestrator"** suchen und deine Orchestrator-URL (z. B. `http://192.168.178.50:5000`) angeben.

Jede Gruppe wird als vollständige `media_player.<gruppe>`-Entität angelegt.

---

## 3. Audio-Broadcasts & Durchsagen (Klingel, Alarm, TTS)

Über die REST API und MQTT können externe Systeme (z. B. Home Assistant Automationen, Scripts, Türklingeln oder Alarmzentralen) kurze Audio-Clips (MP3, WAV, AAC, OGG, FLAC oder Remote-URLs) auf bestimmten Zonen, einzelnen Räumen oder im ganzen Haus abspielen.

### Funktionsweise:
- **Instant Alert / Klingel**: Mit `fadeDurationMs: 0` schaltet der Ton verzögerungsfrei um.
- **Sanfte Durchsagen**: Mit z. B. `fadeDurationMs: 1000` blendet die laufende Musik sanft aus, die Durchsage wird bei der gewünschten Ziellautstärke abgespielt und danach blendet die Musik wieder ein.
- **Zielauswahl**: Angabe von `groupIds` (Gruppen) oder `roomIds` (einzelne Räume). Werden keine Ziele angegeben, wird die Durchsage an **alle aktiven Zonen** im Haus übertragen.
- **Wiederherstellung**: Nach Ende des Audio-Clips werden vorherige Streams, Lautstärken und Mute-Zustände garantiert wiederhergestellt.
- **Optionale Pause**: Mit `pausePlayback: true` wird die aktive Wiedergabe (z. B. Spotify) für die Dauer der Durchsage pausiert und anschließend fortgesetzt.

### Aufruf via REST API

```bash
# Beispiel 1: Türklingel auf Gruppe "Wohnzimmer" mit Lautstärke 80
curl -X POST http://<orchestrator-ip>:5000/api/broadcast/play \
  -H "Content-Type: application/json" \
  -d '{
    "audioUrl": "http://homeassistant:8123/local/doorbell.mp3",
    "groupIds": ["Wohnzimmer"],
    "volume": 80,
    "fadeDurationMs": 0
  }'

# Beispiel 2: Alarm im ganzen Haus (alle Zonen) mit 95% Lautstärke
curl -X POST http://<orchestrator-ip>:5000/api/broadcast/play \
  -H "Content-Type: application/json" \
  -d '{
    "audioUrl": "http://homeassistant:8123/local/alarm.mp3",
    "volume": 95,
    "fadeDurationMs": 0,
    "interrupt": true
  }'

# Notfall-Stopp (bricht laufende Durchsage sofort ab):
curl -X POST http://<orchestrator-ip>:5000/api/broadcast/stop
```

### Aufruf via MQTT

Sende an `<baseTopic>/broadcast/play` (Standard: `audio_orchestrator/broadcast/play`):
- Als einfacher Text: Direkt die Audio-URL (z. B. `http://homeassistant:8123/local/chime.mp3`).
- Als JSON: Vollständiges Konfigurationsobjekt (z. B. `{"audioUrl": "...", "volume": 85, "groupIds": ["Wohnzimmer"]}`).
- An `<baseTopic>/broadcast/stop`: Bricht jede aktive Durchsage sofort ab.

---

## Fazit & Empfehlung

* Möchtest du die **beste visuelle Medien-Erfahrung** in Home Assistant? **Nimm HACS.**
* Möchtest du **Zero-Setup ohne Plugins**, maximale Stabilität über MQTT und flexible Automationen? **Nimm MQTT.**
* Beide Wege können bei Bedarf auch parallel betrieben werden!
