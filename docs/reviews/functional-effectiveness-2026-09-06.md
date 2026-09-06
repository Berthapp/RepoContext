# Funktions- und Wirkungsanalyse RepoContext

Stand: 6. September 2026. Analysiert wurde **v0.10.0**, Commit
`806ba51e24aed14904b799dd01430c9f78672b47` auf `main`.
Arbeitsbranch: `analysis/functional-effectiveness`.

## Beurteilung

**RepoContext ist lauffähig. Der entscheidende Verbesserungsbedarf liegt in
der zuverlässigen Auswahl hilfreichen Kontexts und in der Antwortzeit.**
Das Produkt verfügt bereits über die wesentlichen Funktionen: lokale Suche,
Quelltextausschnitte, Symbole, Beziehungen, Änderungsprüfung, Wiederverwendung,
Agentenanbindung und einen lokalen Nutzungsbericht. Weitere Dateiformate oder
zusätzliche Oberflächen haben aktuell weniger Nutzen als Verbesserungen an
diesen Kernabläufen.

Alle vorhandenen Tests liefen erfolgreich. Zusätzliche Gegenproben finden
dennoch falsche bzw. fehlende Abhängigkeiten, einen Fehler bei reduzierten
Konfigurationen und schwache Ergebnisse bei natürlich formulierten Aufgaben.
Die vorhandenen Tokenkennzahlen belegen einzelne Einsparmechanismen, aber noch
keine bessere oder günstigere Bearbeitung realer Programmieraufgaben.

Dieser Branch enthält die Analyse, Messdaten und ein Reproduktionsskript.
**Produktkorrekturen sind nachfolgend vorgeschlagene Arbeit; sie sind hier
noch nicht implementiert.**

## Was tatsächlich geprüft wurde

| Prüfung | Ergebnis |
| --- | --- |
| Release-Build der gesamten Solution | Erfolgreich |
| .NET-Kerntests | 375 bestanden, 0 fehlgeschlagen, 0 übersprungen |
| .NET-Integrationstests einschliesslich MCP und Evaluation | 218 bestanden, 0 fehlgeschlagen, 0 übersprungen |
| npm-Launcher | 12 bestanden |
| GitHub-CI des Analysebranches auf dem Ausgangscommit | Erfolgreich, [Lauf 34025987352](https://github.com/Berthapp/RepoContext/actions/runs/34025987352) |
| Vollindex des eigenen Repositorys | 362 Dateien, 3'516 Chunks, 2'972 Symbole, 1'681 Kanten; intern 3'091 ms |
| Unveränderter Folgeindex | 0 Dateien neu geparst; trotzdem 340 Graphdateien und 1'681 Kanten neu verarbeitet; intern 263 ms |
| Zusätzliche Funktionsproben | TypeScript-Imports, C#-Namenskollisionen, leere Konfiguration, Änderungen nach dem Indexieren |
| Suche am eigenen Repository | 6 vorab anhand des Codes beschriftete Aufgaben; weitere isolierte Kontrollmessungen |

Lokale Umgebung: Linux x64, .NET SDK 10.0.100 / Runtime 10.0.0, Release-Build.
Die Solution wurde mit dem mitgelieferten MSBuild ausgeführt; die üblichen
`dotnet build`-/`dotnet test`-Befehle sind die entsprechenden Reproduktionswege.
Die Dateien und Ergebnisse stammen aus echten CLI-Aufrufen. Es wurden keine
LLM-Aufgaben ausgeführt. Laufzeiten sind Einzelmessungen, keine p95-Benchmarks.
Die erste Suchserie lief parallel zu Integrationstests; die unten ausdrücklich
als isoliert markierten Kontrollmessungen liefen anschliessend ohne diese Tests.

## 1. Höchste Priorität: Trefferqualität an echten Aufgaben absichern

Pro Aufgabe wurden die erwarteten Implementierungsdateien vor dem CLI-Aufruf
anhand des Codes festgelegt. Aufruf jeweils:

```sh
repoctx context "<Aufgabe>" --detail auto --top 8 --response-budget-tokens 2000 --format json
```

| Aufgabe, unverändert an die CLI übergeben | Erwartete Dateien in der Antwort |
| --- | ---: |
| `exclude generated files and respect gitignore` | 1 von 2 |
| `Generierte Dateien ausschliessen und Gitignore beachten` | 1 von 2 |
| `avoid sending the same source spans twice using receipts` | 0 von 2 |
| `fix response token budget packing` | 1 von 2 |
| `resolve TypeScript module imports to source files` | 0 von 2 |
| `persist and load agent session receipts` | 1 von 1 |

Insgesamt wurden **4 von 11 erwarteten Dateitreffern** geliefert. Nur bei einer
der sechs Aufgaben enthielt die erste Antwort alle festgelegten Dateien.
Das ist ein kleiner diagnostischer Test, keine repräsentative Erfolgsquote des
Produkts und kein Nachweis, dass die übrigen Aufgaben unlösbar wären.
Zusätzliche Suche bzw. Dateizugriffe können die Lücken schliessen, kosten aber
weitere Arbeit.

Besonders anschaulich ist die Importaufgabe. Benötigt werden
`GraphBuilder.cs` und `ReferenceExtractor.cs`. Die budgetierte Antwort enthält
stattdessen `ILanguageParser.cs`, `GraphTests.cs` und `DetailPolicyTests.cs`.
Auch die acht Ergebnisse ohne Antwortlimit enthalten die beiden erwarteten
Dateien nicht. Die gezielte Suche nach `ResolveTsImport` findet dagegen
`GraphBuilder.cs` sofort. Indexierung und Symbolsuche funktionieren hier;
die Auswahl für die natürlich formulierte Aufgabe ist das Problem.

**Empfohlene Änderung:** Einen unabhängigen Aufgabenkorpus mit 30–50 realen
Aufgaben aus C#, TypeScript/Next.js und mehreren Projekten anlegen. Soll-Dateien,
entscheidende Codebereiche und notwendige Beziehungen vor Optimierungen
festhalten. Produktionscode, Tests, Test-Fixtures und Dokumentation bewusst
unterscheiden; Fixtures dürfen für Arbeit am echten Produkt nicht unbemerkt
den Platz der Implementierung einnehmen. Query-Terme mit hoher Häufigkeit wie
`files`, `source` oder `change` weniger dominant behandeln. Ranking- und
Budgeteffekte getrennt messen und anhand dieses Korpus verbessern.

**Abnahme:** Vorher/nachher Recall, gelieferte relevante Zeilen, benötigte
Folgeaufrufe und vollständige Aufgabenerledigung berichten. Eine mögliche
erste Zielvorgabe ist mindestens 90 % Dateirecall bei Top 8 auf dem separat
festgelegten Korpus; dies ist ein Vorschlag, kein bisher erreichter Wert.

Code: [QueryAnalyzer](../../src/RepoContext.Core/Context/QueryAnalyzer.cs),
[ContextEngine: Kandidaten, Ranking, Diversity und Pack](../../src/RepoContext.Core/Context/ContextEngine.cs).

## 2. Höchste Priorität: Budgetierung deutlich beschleunigen

Isolierte Kontrollmessung derselben Importaufgabe auf demselben Index:

| Variante | Gesamtdauer des CLI-Prozesses | Erwartete Implementierungsdateien |
| --- | ---: | ---: |
| JSON, Antwortlimit 2'000 | 22,494 s | 0 von 2 |
| Markdown, Antwortlimit 2'000 | 16,825 s | 0 von 2 |
| JSON, ohne `--response-budget-tokens` | 1,768 s | 0 von 2 |
| JSON, 2'000, zusätzlich `--path src/RepoContext.Core` | 3,297 s | 0 von 2 |
| Symbolsuche `ResolveTsImport` | 0,208 s | `GraphBuilder.cs` gefunden |

Das explizite Antwortlimit verteuert diese Anfrage stark. Das Entfernen des
Limits verbessert in diesem Fall die Laufzeit, löst aber die Trefferlücke nicht.
Es sollte daher nicht die Produktlösung sein.

**Plausible Ursache aus dem Code, noch nicht durch einen Profiler isoliert:**
`SelectCandidates` ruft für vorgeschlagene Varianten wiederholt
`ClassifyOmissions` über die gesamte Kandidatenmenge auf. Dabei werden erneut
Varianten aufgebaut und in `SpanVariant` tokenisiert. Zusätzlich rendert und
tokenisiert `ContextCostModel.Measure` komplette mögliche Antworten.
Bei hunderten Kandidaten entsteht erhebliche wiederholte Arbeit.

**Empfohlene Änderung:** Varianten und unveränderliche Tokenkosten pro Anfrage
zwischenspeichern, Auslassungsmetadaten effizient fortschreiben und aufwendige
Antwortprüfungen reduzieren. Das tatsächlich ausgegebene Ergebnis weiterhin
exakt tokenisieren und gegen das harte Limit prüfen. Keine naive Addition von
BPE-Teilkosten als Ersatz für die abschliessende Prüfung verwenden.

Die JSON-Ausgabe liefert bei einzelnen Spans ausserdem denselben Quelltext in
`spans[].text` und im Kompatibilitätsfeld `snippet`. Ein ausdrücklich versionierter
kompakter Ausgabemodus könnte diese Redundanz entfernen und Platz für relevante
Evidenz schaffen. Bestehende Clients müssen dabei berücksichtigt werden.

**Abnahme:** Isolierte Messungen mit mehreren Wiederholungen auf 300, 3'000 und
30'000 Dateien, mit engen Budgets und vielen Treffern. Bestehende Budget- und
Receipt-Tests bleiben verbindlich. Als erstes Produktziel sind unter zwei
Sekunden für warme Abfragen auf dem eigenen Repository sinnvoll; noch nicht
gemessenes Ziel, keine Garantie.

Code: [ContextEngine.SelectCandidates/ClassifyOmissions/SpanVariant](../../src/RepoContext.Core/Context/ContextEngine.cs),
[ContextCostModel.Measure](../../src/RepoContext.Cli/Output/ContextCostModel.cs),
[ContextOutput](../../src/RepoContext.Cli/Output/ContextOutput.cs).

## 3. Bestätigter Fehler: Fehlende Konfigurationsfelder verlieren Standards

Eine `repoctx.config.json` mit `{}` aktiviert nicht dieselben Einstellungen
wie `repoctx init`. Im Test wurden `.env`, `node_modules/demo/index.js` und
`obj/generated.cs` indexiert. Die Dateien enthielten ausschliesslich Dummytext.
Mit den von `init` geschriebenen Standards sollten sie ausgeschlossen sein.

Ursache: `ConfigStore.Deserialize` deserialisiert nach `RepoctxConfig`.
Dessen Property-Initialisierer setzen `Exclude` und `SensitiveFiles` auf leere
Listen. Die vorgesehenen Standards existieren erst in `CreateDefault()`.
Fehlende JSON-Felder rufen diese Factory nicht auf.

Das ist sowohl eine funktionale Qualitätslücke durch unnötige Datenmengen als
auch ein Vertrauensproblem beim Ausschluss sensibler Dateien. Daraus folgt
keine beobachtete Netzwerkübertragung: RepoContext selbst arbeitet lokal.

**Kleine, klar begrenzte Korrektur:** Einheitliche Standardwerte für neue
Instanzen und `CreateDefault()`. Fehlende Felder übernehmen Standards;
ausdrücklich angegebene leere Listen bleiben eine bewusste Überschreibung.
Zusätzlich ungültige `null`-Werte und negative Grössenlimits verständlich
validieren. Regression über `{}`, Teilkonfiguration und explizite Listen
jeweils durch die tatsächliche Scan-/Index-Pipeline prüfen.

Code: [RepoctxConfig](../../src/RepoContext.Core/Configuration/RepoctxConfig.cs),
[ConfigStore.Deserialize](../../src/RepoContext.Core/Configuration/ConfigStore.cs).

## 4. Wichtige Funktionslücke: TypeScript-Abhängigkeiten

Minimaler Versuch mit `src/session.ts` und drei aufrufenden Dateien:

| Import | Von `related src/session.ts` als Aufrufer erkannt? |
| --- | --- |
| `./session` | Ja |
| `./session.js` | Nein |
| `@/session`, mit `paths: { "@/*": ["./src/*"] }` | Nein |

TypeScript unterstützt die Auflösung von `.js`-Importen zu TypeScript-Quellen
und konfigurierten Pfadzuordnungen; siehe die
[offizielle Modulreferenz](https://www.typescriptlang.org/docs/handbook/modules/reference.html#file-extension-substitution)
und [paths-Dokumentation](https://www.typescriptlang.org/tsconfig/paths.html).
RepoContext hängt in `ResolveTsImport` lediglich Erweiterungen an den
unveränderten Importpfad an. Nichtrelative Pfade werden vorab ignoriert.
Die fehlende Aliasauflösung ist bereits in ADR 0006 als MVP-Grenze dokumentiert.

**Auswirkung:** `related`, Änderungsfolgen und graphgestützter Kontext können
tatsächliche Aufrufer und Tests übersehen. Die Dateien bleiben grundsätzlich
über Text- und Symbolsuche auffindbar.

**Empfohlene Reihenfolge:** Zuerst `.js`/`.mjs`/`.cjs`-Zuordnung zu passenden
Quell- und Deklarationsdateien; danach `tsconfig`-Pfadzuordnungen einschliesslich
`extends` und Projektgrenzen. Nicht unterstützte oder mehrdeutige Importe
sichtbar als unaufgelöst melden. Den bisherigen relativen Kontrollfall und
negative Fälle ohne lokales Ziel mitprüfen.

Code: [GraphBuilder.AddImportEdges/ResolveTsImport](../../src/RepoContext.Core/Graph/GraphBuilder.cs).

## 5. Bestätigte falsche Beziehungen: Gleichnamige C#-Typen

Zwei unabhängige Dateien genügen:

```csharp
// src/A/User.cs
namespace Alpha;
public sealed class User { public string Name => "A"; }

// src/B/User.cs
namespace Beta;
public sealed class User { public string Name => "B"; }
```

`repoctx related src/A/User.cs --format json` meldet `src/B/User.cs` sowohl als
`imports` als auch als `imported_by`. Zwischen diesen Klassen besteht keine
solche Abhängigkeit. Der deklarierte Name `User` wird selbst als Typverwendung
extrahiert; beim Auflösen wird die eigene Datei übersprungen und die andere
gleichnamige Klasse gewählt.

**Empfohlene Änderung:** Deklarationen und tatsächliche Verwendungen trennen,
Kommentare und Strings nicht als echte Typverwendungen behandeln und
Namespace-/Projektinformation einbeziehen. Unsichere Namensheuristiken in der
Ausgabe kennzeichnen. Für mehrdeutige Typen nicht stillschweigend eine echte
Importbeziehung behaupten. Ein vollständiger Roslyn-Adapter kann später folgen;
dieser einfache Fehlfall sollte vorher geschlossen werden.

**Abnahme:** Keine Kanten zwischen den beiden unabhängigen Klassen;
tatsächliche qualifizierte Verwendungen, gleiche Namen in mehreren Projekten
und bestehende Testzuordnungen bleiben korrekt.

Code: [ReferenceExtractor](../../src/RepoContext.Core/Graph/ReferenceExtractor.cs),
[GraphBuilder.AddTypeEdges](../../src/RepoContext.Core/Graph/GraphBuilder.cs).

## 6. Bekannte Betriebsgrenze: Kontext bleibt nach Änderungen alt

Nach dem Indexieren wurde `createSession()` von `OLD_SESSION` auf
`NEW_SESSION` geändert. Die nächste identische Kontextabfrage lieferte
**byteidentisch den alten Quelltext**. `changed --patch` erkannte die Änderung;
nach `index` erschien der neue Inhalt.

Dies ist die dokumentierte Trennung zwischen Indexabfragen und
Arbeitsverzeichnisprüfung, kein neu entdeckter Verstoss gegen den aktuellen
Vertrag. Für zuverlässige Agenten ist sie dennoch eine wichtige Bedienfalle:
Auch ein Branchwechsel oder die Änderung durch einen anderen Prozess kann
den Index veralten lassen, bevor der Agent selbst etwas editiert.

**Empfohlene Änderung:** Eine einfache Freshness-Strategie für den Beginn einer
Aufgabe und für Branchwechsel anbieten, z. B. opt-in `--ensure-fresh` oder eine
von der Integration gesteuerte Aktualisierung. Indexgeneration bzw. ungeprüfte
Aktualität sichtbar machen. Ein MCP-Client ohne Shell kann aktuell
`get_changes` aufrufen, aber kein entsprechendes Indexwerkzeug; für diesen
Anwendungsfall fehlt ein vollständiger Aktualisierungspfad.

Bis dahin: Vor der Aufgabe indexieren, nach Änderungen `changed --patch`
verwenden und bei Bedarf erneut indexieren. Ein Watcher ist eine mögliche
spätere Ergänzung, muss aber nicht der erste Umsetzungsschritt sein.

Code: [CommandSupport.EnsureIndexUsable](../../src/RepoContext.Cli/Commands/CommandSupport.cs),
[ChangeDetector](../../src/RepoContext.Core/Indexing/ChangeDetector.cs),
[McpTools](../../src/RepoContext.Cli/Mcp/McpTools.cs).

## 7. Zusätzliches Zuverlässigkeitsrisiko: Indexzustand ist nicht atomar

**Codebefund, nicht durch einen erzwungenen Prozessabbruch reproduziert:**
Der Indexer schreibt Dateien in einer Transaktion, baut anschliessend den
Graphen auf und aktualisiert danach Metadaten einzeln. `ClearEdges` liegt
ausserhalb der Graphtransaktion. Das Einlesen der vorhandenen Dateien erfolgt
vor der Schreibtransaktion; ein übergreifender Indexer-Lock fehlt.

Ein Abbruch oder konkurrierende Indexläufe können dadurch widersprüchliche
Zwischenstände hinterlassen. `HasValidStateHash` prüft nur das Format des
gespeicherten Hashes, nicht die Übereinstimmung mit dem aktuellen Datenbestand.
Zudem werden Datei-Hash und indexierter Text getrennt eingelesen; eine Änderung
dazwischen kann Inhalt und Identität auseinanderlaufen lassen.

**Empfehlung:** Indexläufe pro Repository serialisieren, Hash und Analyse aus
demselben eingelesenen Inhalt ableiten und eine vollständige Indexgeneration
atomar veröffentlichen. Lesende Abfragen sollten eine konsistente Generation
verwenden. Erst mit Abbruch-, Parallelitäts- und Änderungsproben als behoben
bewerten; ein Lock allein ersetzt die atomare Veröffentlichung nicht.

Code: [Indexer.Run](../../src/RepoContext.Core/Indexing/Indexer.cs),
[GraphBuilder.Rebuild](../../src/RepoContext.Core/Graph/GraphBuilder.cs),
[IndexStore](../../src/RepoContext.Core/Storage/IndexStore.cs).

## 8. Wirkung nachweisen statt nur kleinere Antworten zählen

Die vorhandene Evaluation ist transparent über ihre Grenzen: sechs Dateien,
sieben statische C#/TypeScript-Aufgaben und eine simulierte Strategie. Der
aktuelle gespeicherte Receipt-Versuch sinkt von **1'912 auf 607 Core-Tokens**
bei Wiederholung. Das ist ein konkreter Beleg für günstigere Wiederverwendung,
aber kein Vergleich einer ganzen Programmieraufgabe mit und ohne RepoContext.

`stats` verrechnet Quelltextausschnitte und Outlines mit angenommenen ersetzten
Volltextzugriffen. Liest ein Agent die Datei danach doch vollständig, ist diese
Annahme nicht erfüllt. Die Ausgabe sollte geschätzte Ersparnis direkt von
gemessenen Antwortkosten unterscheiden. Ein pauschaler Tokenfaktor für andere
Modellfamilien ist ebenfalls eine Kalibrierung, kein exakt identischer Tokenizer.

Ein fairer nächster Wirksamkeitstest benötigt:

1. Dieselben vorab definierten Aufgaben und dieselben Erfolgstests mit und ohne
   RepoContext; als Vergleich normale Symbol-/Textsuche plus gezielte Lesezugriffe.
2. Gesamte modellseitig sichtbare Tokens: Anweisungen, Tooldefinitionen,
   Argumente, Antworten und tatsächliche Nachlesevorgänge. MCP-Transportbytes
   getrennt halten; sie sind nicht automatisch zusätzliche Modelleingabe.
3. Korrekte Patches bzw. bestandene Aufgabentests, Fehlversuche, Folgeaufrufe
   und Laufzeiten. Niedrigere Kosten gelten nur zusammen mit gleichbleibender
   Ergebnisqualität als Verbesserung.
4. Für einen späteren Modellversuch identisches Modell und identische
   Einstellungen, mehrere Wiederholungen und Erfolgsquoten je Aufgabentyp.

Dokumentation nachziehen: README nennt noch 1'904 → 609 Tokens;
`docs/token-savings.md` noch sieben Tools und 1'339 Session-Tokens. Der aktuelle
Golden enthält 1'912 → 607 sowie acht Tools und 1'602 Session-Tokens. Solche
Zahlen möglichst aus der Evaluation erzeugen oder auf den aktuellen Bericht
verlinken.

Quellen: [Baseline](../eval/baseline.md), [Manifest](../eval/manifest.md),
[UsageMeter](../../src/RepoContext.Core/Stats/UsageMeter.cs),
[WorkflowSimulator](../../tests/RepoContext.Integration.Tests/Evaluation/WorkflowSimulator.cs).

## Empfohlene Umsetzung

| Paket | Konkretes Ergebnis | Abschlusskriterium |
| --- | --- | --- |
| A: Kleine Korrekturen | Einheitliche Konfigurationsstandards, C#-Deklarationsfehler, `.js`-Importauflösung | Obige Gegenproben als Regressionstests grün |
| B: Hauptnutzen | Unabhängiger Aufgabenkorpus, bessere Auswahl, weniger wiederholte Tokenisierung | Messbar mehr benötigte Evidenz bei gleichem Budget und deutlich kürzere Antwortzeit |
| C: Verlässlicher Betrieb | Konsistente Indexgeneration, Freshness-Pfad, MCP-Aktualisierung, Aliasauflösung | Aufgaben nach Edit/Branchwechsel sowie parallele Indexläufe zuverlässig |
| D: Nachweis | Vollständiger Vergleich ohne/mit RepoContext, klar beschriftete Schätzungen | Gleiche oder bessere Aufgabenergebnisse bei weniger Gesamtaufwand |

Der erste technische Arbeitsschritt sollte Paket A sein; die wichtigste
Produktverbesserung ist Paket B. Architektur, Offlinebetrieb, SQLite und
deterministische Verarbeitung können dabei bestehen bleiben.

## Reproduktion und Messdateien

- [Messdaten mit vollständigen CLI-Antworten](functional-effectiveness-2026-09-06.json)
- [Reproduktionsskript](functional_effectiveness_probe.py), Python 3 und bereits
  gebautes RepoContext; Standardbibliothek, keine zusätzlichen Python-Pakete.

Um die Suchresultate nicht durch diesen Analysebericht selbst zu beeinflussen,
den untersuchten Commit separat auschecken. Das Skript legt Dummy-Fixtures in
einem temporären Verzeichnis an und baut den lokalen Index der angegebenen
Arbeitskopie neu auf. Es ändert keine Produktdateien.

```sh
git worktree add --detach ../RepoContext-audit 806ba51e24aed14904b799dd01430c9f78672b47
dotnet build ../RepoContext-audit/RepoContext.slnx -c Release
dotnet test ../RepoContext-audit/RepoContext.slnx -c Release --no-build
node --test ../RepoContext-audit/npm/repocontext/test/platform.test.mjs
python3 docs/reviews/functional_effectiveness_probe.py --repo ../RepoContext-audit --output ../RepoContext-audit-results.json
```

Laufzeiten separat von den Tests messen. Die ursprünglichen Rohmessungen werden
für diese Analyse unverändert aufbewahrt; spätere Läufe sind neue Beobachtungen.
