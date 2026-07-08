# RepoContext – Produktdokumentation

**Version:** 2.0 (überarbeitet) · **Datum:** 08.07.2026 · **Ergänzendes Dokument:** `repocontext-mvp-spezifikation.md`

**Wesentliche Änderungen gegenüber v1:**

1. Neues Grundprinzip «Deterministisch und erklärbar» als zentrales Differenzierungsmerkmal.
2. Beispiel-Outputs korrigiert: strukturierte, aus dem Index ableitbare Fakten statt Prosa-Zusammenfassungen, die ohne LLM nicht erzeugbar wären.
3. Neuer Abschnitt «Wettbewerb und Differenzierung» (fehlte in v1 vollständig).
4. Monetarisierung umgebaut: Open Core, MCP-Server im Free-Tier, PR Context Pack als konkreter Kaufgrund für den Team-Tier.
5. Verstreute MVP-Angaben durch eine Roadmap ersetzt; technische Details sind in die MVP-Spezifikation ausgelagert.

---

## 1. Was ist RepoContext

> **Local-first, explainable project memory for AI coding agents.**

RepoContext ist ein local-first und selfhostbares Tool, das Projektwissen aus Software-Repositories indexiert und AI Coding Agents gezielt zur Verfügung stellt. Agents müssen nicht mehr grosse Teile eines Repositories in den Context laden, sondern erhalten auf Anfrage genau die relevanten Dateien, Symbole, Tests und Zusammenhänge – mit Begründung für jeden Treffer.

Dadurch:

* werden Tokens gespart,
* werden Antworten schneller,
* treffen Agents weniger falsche Annahmen,
* ist Projektwissen strukturiert verfügbar,
* bleibt Source Code standardmässig lokal.

RepoContext ist selbst kein AI-Modell und verursacht standardmässig keinen eigenen Tokenverbrauch.

---

## 2. Problem

AI Coding Agents arbeiten oft mit zu viel oder zu wenig Kontext:

* Der Agent liest zu viele Dateien, der Prompt wird unnötig gross.
* Hoher Tokenverbrauch, langsame Antworten.
* Fehlende Projektübersicht führt zu falschen Änderungen.
* Unsicherheit, welche Daten an AI-Anbieter gesendet werden.

Je grösser das Repository, desto schwieriger wird es, dem Agent genau das richtige Wissen bereitzustellen – und desto teurer wird jeder Fehlversuch.

---

## 3. Lösung

RepoContext baut einen lokalen, deterministischen Index des Projekts auf: Projektstruktur, wichtige Dateien, Klassen und Funktionen, API-Routen, Abhängigkeiten zwischen Dateien, Tests, Konfigurationen sowie README- und Dokumentationsinhalte.

AI Agents fragen diesen Index über CLI oder MCP ab:

```
$ repoctx context "Ich möchte die Login-Logik ändern"

Relevante Dateien (Top 4 von 1'243 indexierten):
 1. src/auth/login.ts        0.92  Symbol: loginUser · Volltext: login
    → loginUser(credentials: Credentials): Promise<Session>  [Z. 12–48]
    → Tests: src/auth/__tests__/login.test.ts
 2. src/auth/session.ts      0.81  wird von login.ts importiert
 3. src/middleware.ts        0.64  importiert session.ts
 4. src/auth/permissions.ts  0.58  Symbol: checkPermission

Budget: 4 Dateien · ~1'400 geschätzte Tokens
```

Der Agent erhält kompakten, begründeten Kontext statt ein ganzes Repository.

**Wichtige Abgrenzung gegenüber v1:** Die Ausgabe besteht ausschliesslich aus Fakten, die aus dem Index ableitbar sind (Dateien, Symbole, Kanten, Tests, Begründungen). Generierte Prosa-Zusammenfassungen («Die Authentifizierung läuft über JWT») gibt es nicht – dafür wäre ein LLM nötig. Prosa-artige Hinweise stammen, wo vorhanden, aus echten Quellen (README, Docstrings) und werden als Zitat mit Fundstelle ausgegeben.

---

## 4. Grundprinzipien

**1. Local-first.** RepoContext läuft standardmässig lokal im Projekt oder auf dem Entwicklergerät. Source Code wird nicht automatisch an externe Dienste gesendet.

**2. Deterministisch und erklärbar.** Gleiche Query auf gleichem Index ergibt dieselbe Antwort. Jeder Treffer trägt eine maschinenlesbare Begründung (`reasons`). Das macht Ergebnisse reproduzierbar, debugbar und audit-fähig – im Gegensatz zu Embedding-basierten Blackbox-Rankings.

**3. Kein AI-Zwang.** Indexierung, Suche und Analyse funktionieren ohne LLM: Dateisystemanalyse, Parser, Symbolanalyse, Volltextsuche, Dependency-Mapping, lokale Datenbank.

**4. Selfhostable.** Für Teams kann ein gemeinsamer RepoContext-Server im eigenen Netzwerk betrieben werden.

**5. Agent-friendly.** Schnittstellen: CLI (universell, jeder Agent mit Shell-Zugriff), MCP-Server, lokale HTTP-API, später CI/CD- und IDE-Integration.

**6. Minimal Context.** So wenig Kontext wie möglich, so viel Kontext wie nötig – mit hartem Token-Budget pro Antwort.

---

## 5. Was RepoContext nicht ist

RepoContext ist kein Ersatz für Claude Code, Cursor, Copilot oder andere Agents und kein eigenes AI-Modell. Es ist ein Kontext-Layer zwischen Repository und Agent:

```
Repository
    ↓
RepoContext Index
    ↓
AI Agent
    ↓
LLM
```

---

## 6. Wettbewerb und Differenzierung

*Stand: Juli 2026 – vor Launch aktualisieren, das Feld bewegt sich schnell.*

| Ansatz | Beispiele | Abgrenzung RepoContext |
|--------|-----------|------------------------|
| Agent-interne Suche zur Laufzeit | Claude Code, Copilot | Funktioniert, liest aber pro Aufgabe trotzdem viele Dateien. RepoContext ist **komplementär**: Der vorverdaute Index ist via CLI/MCP aus jedem Agent nutzbar. |
| Repo-Map im Agent | aider (repo-map) | Konzeptionell am nächsten (ebenfalls tree-sitter-basiert), aber an aider gebunden – kein eigenständiger Layer, kein Team-Index. |
| LSP-/MCP-Symbolnavigation | Serena | Session-orientierte Navigation; kein persistenter, teamweiter Index, kein PR-Kontext. |
| Embedding-Indexing | Cursor Codebase-Index | Blackbox-Ranking, Verarbeitung teils in der Cloud, toolgebunden. |
| Repo-Packer | Repomix | Gegenteiliger Ansatz: maximaler statt minimaler Kontext. |
| Code-Intelligence-Plattform | Sourcegraph | Mächtig, aber schwergewichtig, serverzentriert, anderes Preissegment. |

**USP-Dreiklang:**

1. **Deterministisch und erklärbar** – jeder Treffer mit Begründung, reproduzierbar, audit-fähig.
2. **Local-first und agent-agnostisch** – funktioniert offline, mit jedem Agent, ohne Vendor-Bindung.
3. **Team-Wissensschicht mit PR-Kontext** – selfhosted, zentraler Index über mehrere Repositories; keines der obigen Tools bedient genau das.

**Ehrliche Einordnung:** Das Zeitfenster ist real. Agents verbessern ihre eigene Repo-Suche laufend. Deshalb bleibt das MVP klein und die Kernhypothese wird früh gemessen (Token-Benchmark, siehe MVP-Spezifikation Kap. 13).

---

## 7. Tokenverbrauch

RepoContext selbst verursacht keinen Tokenverbrauch. Tokens entstehen erst, wenn ein Agent oder LLM Text verarbeitet:

* RepoContext liest Dateien lokal → keine Tokens
* RepoContext erstellt und durchsucht den Index → keine Tokens
* RepoContext gibt 20 Zeilen Kontext an den Agent → diese 20 Zeilen können Tokens verursachen

Der Nutzen: Der Agent muss nicht 50 Dateien lesen, sondern bekommt nur die relevanten Informationen – innerhalb eines konfigurierbaren Token-Budgets.

---

## 8. Datenschutz

RepoContext sendet standardmässig keine Repository-Daten an externe Dienste und enthält im Kern keine Telemetrie.

**Wichtige, ehrliche Einschränkung:** RepoContext kann verhindern, dass das eigene Tool Daten verschickt. Es kann nicht verhindern, dass ein externer AI Agent die erhaltenen Ausschnitte an einen AI-Anbieter sendet.

Für maximale Sicherheit wird empfohlen: lokales oder selfhosted RepoContext, interner Agent, lokales oder unternehmenseigenes LLM, `.repoctxignore` für sensible Dateien.

Zusätzlich: Der Index selbst (`.repoctx/`) enthält Code-Ausschnitte und ist ein sensibles Artefakt – er wird bei `repoctx init` automatisch in `.gitignore` eingetragen. Dateien aus `.repoctxignore` und `sensitiveFiles` werden weder inhaltlich noch als Pfad indexiert.

---

## 9. Architektur

**Lokale Variante:**

```
Projekt
├── .repoctx/
│   ├── index.db          (SQLite + FTS5)
│   └── config-cache
├── repoctx.config.json
└── Source Code
```

Ablauf: `repoctx init` → `repoctx index` → Agent fragt Kontext ab → RepoContext liefert begründete Auswahl.

**Selfhosted Team-Variante (post-MVP):**

```
Git Repositories
    ↓
RepoContext Worker (CI-getriggert)
    ↓
Selfhosted RepoContext Server
    ↓
Team Index Database
    ↓
MCP / API / CI Integration
    ↓
AI Agents
```

---

## 10. Produktumfang und Roadmap

| Stufe | Inhalt | Details |
|-------|--------|---------|
| **MVP** | CLI, lokaler Index (inkrementell), `search` / `related` / `context` / `architecture`, Formate text/json/md, Privacy-Features, optional MCP-Server. Sprachen: TypeScript/JavaScript + C#. | `repocontext-mvp-spezifikation.md` |
| **v0.2** | MCP-Server (falls im MVP gestrichen), Watch-Mode, weitere Sprachen (Python, Go) | – |
| **v0.3** | `pr-context` + CI-Integration – die Brücke zum Team-Produkt (Kap. 12) | – |
| **v0.4** | Optionale **lokale** Embeddings (opt-in, bleibt offline), Roslyn-Adapter für echte C#-Referenzauflösung | – |
| **Team-Server** | Gemeinsamer Index über mehrere Repositories, Git-Integrationen, Rollen/Rechte, SSO, Audit Logs | erst nach bewiesenem Einzelnutzer-Wert |

---

## 11. Hauptkomponenten

**CLI** – der Einstieg und die universelle Agent-Schnittstelle:

```
repoctx init
repoctx index
repoctx search "authentication"
repoctx related src/services/UserService.cs
repoctx context "Ich will die Zahlungslogik ändern"
repoctx architecture
```

**Lokaler Index** – strukturierte Projektdaten: Dateipfade und -typen, Symbole (Klassen, Funktionen, Interfaces, Routen), Imports/Exports, Test-Verknüpfungen, Doku-Abschnitte, Abhängigkeiten zwischen Dateien.

**MCP-Server** – direkter Tool-Zugriff für Agents, dünner Wrapper über derselben Query-Engine:

```
repoctx.search
repoctx.get_context
repoctx.get_related_files
(später: get_architecture, get_tests, get_pr_context)
```

**Selfhosted Server (post-MVP)** – für Teams: mehrere Repositories, gemeinsamer Index, Rollen und Rechte, SSO, Audit Logs, CI/CD-Integration, zentrale Regeln – ohne Code-Upload zum Anbieter.

---

## 12. Stärkster Team-Use-Case: PR Context Pack (v0.3)

RepoContext erstellt automatisch einen Kontextbericht pro Pull Request – für Agents, Reviewer und CI:

```
$ repoctx pr-context 123

Geänderte Dateien:
 - src/auth/login.ts
 - src/auth/session.ts
Betroffene Bereiche (aus dem Import-Graph):
 - src/middleware.ts, src/auth/permissions.ts  (direkte Abhängige)
Relevante Tests:
 - auth.test.ts, session.test.ts  (verknüpft, im PR nicht angepasst)
Hinweise (Graph-Fakten):
 - session.ts hat 4 direkte Abhängige, davon 1 ohne verknüpfte Tests
Empfohlener Agent-Kontext:
 - 6 Dateien, ~2'100 Tokens  →  repoctx context --pr 123
```

Auch hier gilt: Hinweise sind Graph-Fakten («N Abhängige, M ohne Tests»), keine generierten Risiko-Einschätzungen in Prosa. Das PR Context Pack ist der konkrete Kaufgrund für den Team-Tier: Es liefert Nutzen, den weder ein einzelner Agent noch die lokale Variante bieten kann.

---

## 13. Tech-Stack (Kurzfassung)

.NET 10 (LTS) · SQLite + FTS5 · tree-sitter für TypeScript/JavaScript und C# (Roslyn später als semantischer Adapter) · System.CommandLine · offizielles MCP-C#-SDK · Distribution als `dotnet global tool` plus self-contained Binaries für Windows, macOS und Linux.

Begründungen und Alternativen: MVP-Spezifikation, Kap. 8.

---

## 14. Beispiel-Konfiguration

```json
{
  "include": ["src", "app", "lib", "docs"],
  "exclude": ["node_modules", "dist", "bin", "obj", ".next", ".git"],
  "respectGitignore": true,
  "sensitiveFiles": [".env*", "*.secret.*", "appsettings.Production.json"],
  "indexing": {
    "maxFileSizeKb": 512,
    "includeTests": true,
    "includeDocs": true
  },
  "ranking": {
    "weights": { "fts": 0.4, "symbol": 0.3, "graph": 0.2, "path": 0.1 },
    "synonyms": { "zahlung": ["payment", "billing"] }
  }
}
```

Zusätzlich `.repoctxignore` (gitignore-Syntax):

```
.env
*.secret.json
/private
/certificates
/customer-data
```

---

## 15. Monetarisierung (überarbeitet)

**Leitprinzip: Nichts, was Adoption treibt, ist bezahlpflichtig.** Empfohlenes Modell: Open Core – der Kern als Open Source (Apache-2.0), Monetarisierung über Team- und Enterprise-Funktionen.

| Tier | Zielgruppe | Inhalt | Preis-Hypothese |
|------|------------|--------|-----------------|
| **Free (OSS-Kern)** | Einzelentwickler | CLI, lokaler Index, `search`/`related`/`context`/`architecture`, **MCP-Server**, alle Privacy-Features | 0 |
| **Pro** *(optional, später)* | Power-User | Lokale Embeddings, Roslyn-Semantik, Multi-Repo-Workspace, erweiterte Reports | ~5–10 USD/Monat – erst einführen, wenn Nachfrage sichtbar |
| **Team** | kleine und mittlere Teams | Selfhosted Server, gemeinsamer Index, GitHub/GitLab/Azure-DevOps-Integration, CI-Indexing, **PR Context Packs**, Teamregeln, Rollen und Rechte | ~15–25 USD/User/Monat |
| **Enterprise** | grössere Firmen | On-Prem, SSO, Audit Logs, Custom Parser, Security Review, Support, private Deployments | individuell |

**Änderungen gegenüber v1 und Begründung:**

* **MCP von Pro → Free.** MCP ist der Haupt-Adoptionskanal für Agents; ihn zu gaten würgt die Verbreitung ab, bevor sie beginnt.
* **Pro stark abgespeckt und optional.** Zahlungsbereitschaft bei Einzelentwicklern ist notorisch tief; der Free-Tier muss vollständig genug sein, dass das Tool geliebt wird.
* **PR Context Pack vom «Use Case» zum konkreten Team-Kaufgrund gemacht.**
* **Preise sind Hypothesen** (Anker: gängige Developer-Tools liegen bei 19–20 USD/User/Monat) – vor Launch mit 5–10 Zielkunden validieren.

**Monetarisierungspfad:** Free-Tool wird vom Entwickler geliebt → Entwickler bringt es ins Team → gemeinsamer Index und PR-Kontext rechtfertigen die Team-Lizenz → Compliance-Anforderungen (SSO, Audit, On-Prem) führen zu Enterprise.

---

## 16. Positionierung und Messaging

**Kategorie:** Kontext-Layer / Project Memory für AI Coding Agents.

**One-Liner (EN):** *Local-first, explainable project memory for AI coding agents.*

**One-Liner (DE):** Der deterministische, erklärbare Kontext-Layer für AI Coding Agents – local-first und selfhostbar.

**Drei Kernbotschaften:**

1. **Weniger lesen, besser entscheiden.** Der Agent bekommt nur Relevantes – mit Begründung für jeden Treffer.
2. **Keine Blackbox.** Deterministisch, reproduzierbar, audit-fähig. Das kann kein Embedding-Index.
3. **Dein Code bleibt bei dir.** Offline, selfhostbar, keine Telemetrie.

**Nicht positionieren als:** AI-Tool, Copilot-Konkurrent oder Code-Suchmaschine. RepoContext macht bestehende Agents besser, statt mit ihnen zu konkurrieren.

---

## 17. Elevator Pitch

**Kurzversion (1 Satz):**

> RepoContext macht Repositories AI-ready: Ein lokaler, erklärbarer Index liefert Coding Agents genau den Kontext, den sie brauchen – ohne dass Source Code das Gerät verlässt.

**Langversion:**

> RepoContext ist ein local-first, selfhostbarer Kontext-Layer für AI Coding Agents. Das Tool indexiert Repositories lokal – deterministisch und ohne eigenes LLM – und liefert Agents auf Anfrage genau die relevanten Dateien, Symbole und Tests, mit nachvollziehbarer Begründung für jeden Treffer. Agents lesen dadurch weniger, verbrauchen weniger Tokens und treffen bessere Entscheidungen, während der Source Code das eigene Gerät nicht verlässt. Für Teams gibt es RepoContext als selfhosted Server mit gemeinsamem Index über mehrere Repositories und automatischen Kontextberichten pro Pull Request.
