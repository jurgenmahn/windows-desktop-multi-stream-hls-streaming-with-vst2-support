# Audio Processor And Streamer - Handleiding

Deze handleiding beschrijft alle instellingen in het configuratiescherm van de applicatie.

---

## Aanbevolen Instellingen (Snel Starten)

Voor de beste resultaten en maximale compatibiliteit raden wij de volgende instellingen aan:

- **Stream Formaat:** HLS
- **Container Formaat:** fMP4
- **Audio Codec:** AAC
- **Encoding Profielen:** 64, 128, 192, 256 kbps
- **Buffer Grootte:** 20ms (1024 samples)
- **Segment Duur:** 2 seconden
- **Playlist Grootte:** 5 segmenten

Deze configuratie biedt:
- Brede compatibiliteit (iOS, Android, Sonos, browsers)
- Goede audio kwaliteit met adaptive bitrate
- Acceptabele latency (~10-15 seconden)
- Efficiënte bandbreedte door fMP4 container

---

## Inhoudsopgave

- [Stream Instellingen](#stream-instellingen)
- [Stream Formaat (HLS vs DASH)](#stream-formaat-hls-vs-dash)
- [Container Formaat](#container-formaat)
- [Audio Invoer](#audio-invoer)
- [Sample Rate](#sample-rate)
- [Buffer Grootte](#buffer-grootte)
- [VST Plugins](#vst-plugins)
- [Encoding Profielen](#encoding-profielen)
- [Globale Instellingen](#globale-instellingen)
- [Geavanceerde Opties](#geavanceerde-opties)

---

## Stream Instellingen

### Stream Naam
De naam van de stream zoals deze wordt weergegeven in de applicatie en op de streams-pagina.

### Stream Pad (URL)
Het URL-pad waarop de stream beschikbaar is. Bijvoorbeeld: `/radio1` resulteert in `http://jouwdomein:8080/streams/radio1/`.

---

## Stream Formaat (HLS vs DASH)

### HLS (HTTP Live Streaming)

**Beschrijving:**
HLS is ontwikkeld door Apple en is het meest breed ondersteunde streaming protocol.

**Compatibiliteit:**
| Platform | Ondersteuning |
|----------|---------------|
| iOS / Safari | Native (beste keuze) |
| Android | Via hls.js of ExoPlayer |
| Chrome/Firefox/Edge | Via hls.js |
| Sonos | Volledig ondersteund |
| Smart TV's | Breed ondersteund |
| Oudere apparaten | Beste compatibiliteit |

**Voordelen:**
- Werkt op vrijwel alle apparaten
- Native ondersteuning in Safari en iOS
- Breed ondersteund door hardware players
- Betrouwbaar en bewezen technologie

**Nadelen:**
- Iets hogere latency dan DASH
- Minder flexibel qua codec ondersteuning

**Aanbeveling:** Gebruik HLS voor maximale compatibiliteit, vooral als je Sonos, iOS of oudere apparaten wilt ondersteunen.

---

### DASH (Dynamic Adaptive Streaming over HTTP)

**Beschrijving:**
DASH is een open standaard (ISO) en wordt vooral gebruikt voor video streaming.

**Compatibiliteit:**
| Platform | Ondersteuning |
|----------|---------------|
| Chrome/Firefox/Edge | Via dash.js |
| Android | Via ExoPlayer |
| iOS / Safari | Beperkt (geen native) |
| Sonos | Niet ondersteund |
| Smart TV's | Wisselend |

**Voordelen:**
- Open standaard (niet gebonden aan Apple)
- Flexibeler qua codec ondersteuning
- Geschikt voor moderne web applicaties

**Nadelen:**
- Geen native iOS/Safari ondersteuning
- Niet ondersteund door Sonos
- Minder breed ondersteund op hardware players

**Aanbeveling:** Gebruik DASH alleen als je specifiek moderne browsers target en geen iOS/Sonos ondersteuning nodig hebt.

---

## Container Formaat

### MPEG-TS (.ts segmenten)

**Beschrijving:**
MPEG Transport Stream is het traditionele container formaat voor HLS.

**Kenmerken:**
- Origineel formaat voor HLS
- Iets grotere bestandsgrootte door overhead
- Maximale compatibiliteit met oudere apparaten
- Alleen beschikbaar bij HLS (niet bij DASH)

**Compatibiliteit:**
- Alle apparaten die HLS ondersteunen
- Sonos (volledig)
- Oudere Smart TV's
- Hardware streaming apparaten

**Aanbeveling:** Gebruik MPEG-TS voor maximale compatibiliteit met oudere apparaten en Sonos.

---

### fMP4 (.m4s segmenten)

**Beschrijving:**
Fragmented MP4 is het moderne container formaat, ook bekend als CMAF (Common Media Application Format).

**Kenmerken:**
- Efficiëntere compressie (kleinere bestanden)
- Snellere opstarttijd
- Betere ondersteuning voor moderne codecs (zoals Opus)
- Vereist voor DASH
- Ondersteund door moderne HLS implementaties

**Compatibiliteit:**
- Moderne browsers (Chrome, Firefox, Edge, Safari 10+)
- iOS 10+
- Android 5+
- Sonos (beperkt - nieuwere modellen)

**Aanbeveling:** Gebruik fMP4 voor moderne applicaties waar je de beste kwaliteit/bestandsgrootte ratio wilt.

---

## Audio Invoer

### Audio Invoer Apparaat

Selecteer het audio invoer apparaat:

**WASAPI (Windows Audio Session API):**
- `[WASAPI Loopback]` - Neemt de systeemgeluidsuitvoer op (wat je hoort)
- `[WASAPI] Apparaatnaam` - Directe opname van een invoerapparaat (microfoon, line-in)

**ASIO (Audio Stream Input/Output):**
- `[ASIO] Apparaatnaam` - Professionele audio interface met lage latency
- Vereist ASIO drivers (vaak meegeleverd met audio interfaces)

---

## Sample Rate

De sample rate bepaalt de audiokwaliteit en moet overeenkomen met je bronmateriaal.

| Sample Rate | Gebruik |
|-------------|---------|
| **44100 Hz** | CD-kwaliteit, geschikt voor muziek |
| **48000 Hz** | Standaard voor video/broadcast, aanbevolen |
| **96000 Hz** | Hoge kwaliteit productie (zelden nodig voor streaming) |

**Aanbeveling:** Gebruik 48000 Hz tenzij je specifiek 44100 Hz bronmateriaal hebt.

**Let op:** Bij WASAPI loopback wordt automatisch de sample rate van het Windows audio systeem gebruikt, ongeacht deze instelling.

---

## Buffer Grootte

De buffer grootte bepaalt de latency en stabiliteit van de audio opname.

### WASAPI Aanbevelingen

| Buffer | Latency | Aanbeveling |
|--------|---------|-------------|
| 256 samples | ~5ms | Niet aanbevolen (kan haperen) |
| 512 samples | ~10ms | Alleen voor snelle systemen |
| **1024 samples** | ~20ms | **Aanbevolen voor de meeste systemen** |
| 2048 samples | ~40ms | Voor oudere/tragere systemen |

WASAPI is flexibeler met buffer groottes maar kan bij te kleine buffers audio dropouts veroorzaken.

### ASIO Aanbevelingen

| Buffer | Latency | Aanbeveling |
|--------|---------|-------------|
| **256 samples** | ~5ms | **Aanbevolen voor ASIO** |
| 512 samples | ~10ms | Veilige keuze |
| 1024 samples | ~20ms | Voor oudere systemen |
| 2048 samples | ~40ms | Zelden nodig |

ASIO is ontworpen voor lage latency en kan stabiel draaien met kleine buffers.

**Belangrijke tips:**
- Begin met de aanbevolen waarde en pas aan indien nodig
- Bij audio glitches/klikken: verhoog de buffer grootte
- De buffer grootte moet overeenkomen met je VST plugin instellingen
- Sommige ASIO drivers hebben hun eigen buffer instelling in de driver configuratie

---

## VST Plugins

### Beschrijving
Je kunt VST 2.x plugins toevoegen om de audio te bewerken voordat deze wordt geëncodeerd. Plugins worden in volgorde toegepast (chain).

### Gebruik
1. Klik op **"Add VST Plugin..."** om een .dll bestand te selecteren
2. Optioneel: selecteer een preset (.fxp of .fxb bestand) voor de plugin
3. Plugins kunnen worden verwijderd met de **X** knop
4. De volgorde van plugins bepaalt de verwerkingsvolgorde

### Tips
- Plaats VST plugins in de `Plugins` map voor eenvoudig beheer
- Test plugins eerst in een DAW om de juiste instellingen te vinden
- Exporteer presets vanuit je DAW en laad ze hier

---

## Encoding Profielen

Je kunt meerdere encoding profielen aanmaken voor adaptive bitrate streaming. Elke profiel genereert een aparte audio stream.

### Audio Codecs

#### AAC (Advanced Audio Coding)

**Beschrijving:**
De meest universele audio codec voor streaming.

**Compatibiliteit:**
| Platform | Ondersteuning |
|----------|---------------|
| iOS / Safari | Native |
| Android | Native |
| Chrome/Firefox/Edge | Volledig |
| Sonos | Volledig |
| Smart TV's | Volledig |

**Aanbevolen Bitrates:**
| Kwaliteit | Bitrate | Gebruik |
|-----------|---------|---------|
| Laag | 64 kbps | Spraak, podcasts |
| Normaal | 128 kbps | Algemeen gebruik, achtergrondmuziek |
| Hoog | 192 kbps | Muziek streaming |
| Zeer hoog | 256 kbps | Hoge kwaliteit muziek |
| Maximum | 320 kbps | Maximale kwaliteit |

**Aanbeveling:** AAC 128-192 kbps voor de beste balans tussen kwaliteit en bandbreedte.

---

#### MP3 (MPEG Audio Layer III)

**Beschrijving:**
De klassieke audio codec met maximale compatibiliteit.

**Compatibiliteit:**
| Platform | Ondersteuning |
|----------|---------------|
| Alle platforms | Volledig |
| Oudere apparaten | Beste keuze |
| Sonos | Volledig |

**Aanbevolen Bitrates:**
| Kwaliteit | Bitrate | Gebruik |
|-----------|---------|---------|
| Laag | 96 kbps | Spraak |
| Normaal | 128 kbps | Algemeen gebruik |
| Hoog | 192 kbps | Muziek |
| Zeer hoog | 256 kbps | Hoge kwaliteit |
| Maximum | 320 kbps | Maximale kwaliteit |

**Aanbeveling:** MP3 320 kbps als je maximale compatibiliteit nodig hebt met oudere apparaten.

**Let op:** MP3 is minder efficiënt dan AAC - dezelfde kwaliteit vereist ~20% meer bitrate.

---

#### Opus

**Beschrijving:**
Moderne, open-source codec met uitstekende kwaliteit bij lage bitrates.

**Compatibiliteit:**
| Platform | Ondersteuning |
|----------|---------------|
| Chrome/Firefox | Volledig |
| Edge | Volledig |
| iOS / Safari | iOS 17+ / Safari 15+ |
| Android | Android 5+ |
| Sonos | **Niet ondersteund** |
| Oudere apparaten | Beperkt |

**Aanbevolen Bitrates:**
| Kwaliteit | Bitrate | Gebruik |
|-----------|---------|---------|
| Laag | 32 kbps | Spraak (uitstekend) |
| Normaal | 64 kbps | Spraak/muziek |
| Hoog | 96 kbps | Muziek (zeer goed) |
| Zeer hoog | 128 kbps | Hoge kwaliteit muziek |
| Maximum | 192 kbps | Maximale kwaliteit |

**Aanbeveling:** Opus 96-128 kbps voor moderne browsers met beperkte bandbreedte.

**Let op:** Gebruik Opus alleen met fMP4 container formaat. Niet geschikt voor Sonos of oudere apparaten.

---

### Codec Vergelijking

| Codec | Kwaliteit bij 128kbps | Compatibiliteit | Container |
|-------|----------------------|-----------------|-----------|
| AAC | Goed | Uitstekend | MPEG-TS, fMP4 |
| MP3 | Redelijk | Perfect | MPEG-TS, fMP4 |
| Opus | Uitstekend | Beperkt | fMP4 only |

### Aanbevelingen per Scenario

| Scenario | Codec | Bitrate | Container |
|----------|-------|---------|-----------|
| Sonos + alle apparaten | AAC | 192 kbps | MPEG-TS |
| Moderne browsers | Opus | 128 kbps | fMP4 |
| Maximum compatibiliteit | MP3 | 320 kbps | MPEG-TS |
| Balans kwaliteit/compat. | AAC | 192 kbps | fMP4 |
| Spraak/podcast | AAC | 64 kbps | MPEG-TS |

---

## Globale Instellingen

### Base Domain
Het publieke adres waarop je streams bereikbaar zijn, inclusief poortnummer.

**Voorbeelden:**
- `http://localhost:8080` - Lokaal testen
- `http://192.168.1.100:8080` - LAN toegang
- `http://mijnradio.nl:8080` - Publieke toegang

### Web Server Poort
De TCP poort waarop de ingebouwde webserver luistert.

**Standaard:** 8080

**Let op:** Poorten onder 1024 vereisen administrator rechten op Windows.

### Stream Output Directory
De map waar alle HLS/DASH bestanden worden opgeslagen.

**Standaard:** `hls_output` (relatief aan de applicatie map)

### Segment Duur (seconden)
De lengte van elk audio segment in seconden.

| Waarde | Latency | Bestandsgrootte | Aanbeveling |
|--------|---------|-----------------|-------------|
| 1 sec | Zeer laag | Klein | Niet aanbevolen (veel overhead) |
| **2 sec** | Laag | Normaal | **Aanbevolen** |
| 4 sec | Gemiddeld | Groter | Standaard HLS |
| 6+ sec | Hoog | Groot | Alleen voor on-demand |

**Aanbeveling:** 2 seconden voor live streaming met acceptabele latency.

### Playlist Grootte (segmenten)
Het aantal segmenten dat in de playlist wordt bewaard.

**Standaard:** 5 segmenten

**Berekening totale buffer:** Segment duur × Playlist grootte
Voorbeeld: 2 sec × 5 = 10 seconden aan audio in de buffer

**Let op:** Hogere waarden gebruiken meer schijfruimte en geheugen.

### Streams Pagina Pad
Het URL-pad naar de overzichtspagina met alle streams.

**Standaard:** `/hls/`

**Voorbeeld:** Met pad `/hls/` wordt de pagina bereikbaar op `http://localhost:8080/hls/`

### Monitor Output Apparaat
Selecteer een audio uitvoer apparaat om de verwerkte audio lokaal te beluisteren.

- **(Default Device)** - Gebruikt het standaard Windows audio apparaat
- Selecteer een specifiek apparaat voor dedicated monitoring

> **⚠️ WAARSCHUWING: Audio Loop Gevaar**
>
> Zorg ervoor dat het monitor output apparaat **NIET** hetzelfde is als het apparaat dat wordt gebruikt voor WASAPI Loopback opname. Als je bijvoorbeeld de audio van je speakers opneemt via WASAPI Loopback en diezelfde speakers selecteert als monitor output, ontstaat er een audio feedback loop (echo/piep geluid dat steeds luider wordt).
>
> **Veilige configuratie:**
> - Stream input: `[WASAPI Loopback] Speakers` (neemt speaker audio op)
> - Monitor output: `Headphones` of een ander apparaat (NIET de speakers)
>
> **Onveilige configuratie (vermijden!):**
> - Stream input: `[WASAPI Loopback] Speakers`
> - Monitor output: `Speakers` ← Dit veroorzaakt een audio loop!

---

## Geavanceerde Opties

### Lazy Processing (On-Demand)
Wanneer ingeschakeld wordt audio alleen verwerkt wanneer er daadwerkelijk luisteraars verbonden zijn.

**Voordelen:**
- Bespaart CPU wanneer niemand luistert
- Minder belasting op het systeem

**Nadelen:**
- ~2 seconden vertraging bij eerste verbinding
- Kan korte onderbrekingen veroorzaken als alle luisteraars disconnecten

**Aanbeveling:** Uitschakelen voor continue live streams, inschakelen voor streams die niet constant beluisterd worden.

### Debug Audio Recording
Wanneer ingeschakeld worden WAV bestanden aangemaakt voor troubleshooting:

| Bestand | Inhoud |
|---------|--------|
| `debug_before_vst.wav` | Ruwe audio van de soundcard |
| `debug_input.wav` | Audio na VST verwerking |
| `debug_output.wav` | FFmpeg encoded output |

**Waarschuwing:** Deze bestanden zijn ongecomprimeerd en groeien continu. Schakel uit wanneer niet nodig om schijfruimte te besparen.

---

## Veelgestelde Vragen

### Welke instellingen voor Sonos?
```
Stream Formaat: HLS
Container: MPEG-TS
Codec: AAC
Bitrate: 192 kbps
```

### Welke instellingen voor iOS?
```
Stream Formaat: HLS
Container: fMP4 (of MPEG-TS)
Codec: AAC
Bitrate: 128-192 kbps
```

### Welke instellingen voor minimale latency?
```
Segment Duur: 2 seconden
Playlist Grootte: 3 segmenten
Buffer Grootte: 256-512 samples (ASIO)
```

### Welke instellingen voor beste kwaliteit?
```
Codec: AAC of Opus
Bitrate: 256-320 kbps
Container: fMP4
Sample Rate: 48000 Hz
```

### Audio hapert of valt weg?
1. Verhoog de buffer grootte
2. Controleer of VST plugins niet te veel CPU gebruiken
3. Probeer een hogere segment duur

### Stream start niet?
1. Controleer of het audio invoer apparaat correct is geselecteerd
2. Controleer of FFmpeg correct is geïnstalleerd (in `FFmpeg/bin/` map)
3. Controleer de webserver poort (niet al in gebruik door andere software)
