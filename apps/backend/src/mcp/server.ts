import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';

import { loadConfig } from '../infrastructure/config';
import { DETECTIVE_VERSION } from '../infrastructure/version';
import { Options } from '../options/options';
import { calcChangeCoupling } from '../services/change-coupling';
import { calcCoupling } from '../services/coupling';
import { inferFolders } from '../services/folders';
import {
  aggregateHotspots,
  findHotspotFiles,
  HotspotCriteria,
  ComplexityMetric,
} from '../services/hotspot';
import { updateLogCache, isStale } from '../services/log-cache';
import { calcModuleInfo } from '../services/module-info';
import { calcTeamAlignment } from '../services/team-alignment';
import {
  runTrendAnalysis,
  formatTrendAnalysisForAPI,
} from '../services/trend-analysis';

/**
 * Server-level guidance sent to the client during `initialize`. It gives the
 * LLM the domain context plus an intent -> tool mapping so it can decide which
 * tool(s) to call for a given (possibly German) user request.
 */
const DETECTIVE_MCP_INSTRUCTIONS = `Detective analysiert die Architektur und Evolution von JavaScript/TypeScript-Repositories anhand des Quellcodes und der Git-Historie.

Zentrale Begriffe:
- Scope/Modul: ein per Konfiguration definierter Ordner-Praefix, der eine fachliche Domaene/ein Feature repraesentiert.
- Strukturelle Kopplung: Imports zwischen Scopes (statische Abhaengigkeiten).
- Logische/zeitliche Kopplung (Change Coupling): Scopes, die haeufig im selben Commit geaendert werden.
- Kohaesion: wie stark ein Scope intern zusammenhaengt (interne Importe).
- Hotspot: Datei mit hoher Komplexitaet UND hoher Aenderungsfrequenz (Score = Komplexitaet * Commits).

Intent -> Tool-Auswahl:
- "Domaenen-Schnitte / Modulgrenzen / Bounded Contexts / Architektur-Schnitt bewerten": zuerst coupling_get (strukturelle Kopplung + Kohaesion je Scope), dann changeCoupling_get (logische Kopplung). Ergaenzend modules_get (Groesse/Balance der Scopes) und teamAlignment_get (Conway's Law). Interpretation: hohe Kohaesion innerhalb + geringe Kopplung zwischen Scopes = guter Schnitt; viel Off-Diagonal-Kopplung = schlechter Schnitt.
- "Refactoring-Kandidaten / problematische Dateien / technische Schulden finden": hotspots_find (Dateiliste) bzw. hotspots_aggregate (pro Modul aggregiert).
- "Qualitaetsentwicklung / Trend ueber die Zeit": trendAnalysis_run.
- "Eine konkrete Datei tief analysieren": xray_get (Schema via xray_schema).
- "Projektstruktur / vorhandene Scopes verstehen": folders_get und config_read.

Hinweise:
- Viele Tools werten die Git-Historie aus; mit limitCommits/limitMonths laesst sich der Zeitraum einschraenken.
- Wenn die Cache-Lage unklar ist, cache_status pruefen und ggf. cache_update aufrufen, bevor historienbasierte Tools genutzt werden.`;

type Limits = {
  limitCommits: number | null;
  limitMonths: number | null;
};

const limitsSchema = z.object({
  limitCommits: z.number().int().nullable().optional(),
  limitMonths: z.number().int().nullable().optional(),
});
const limitsSchemaShape = {
  limitCommits: z
    .number()
    .int()
    .nullable()
    .optional()
    .describe(
      'Nur die letzten N Commits der Git-Historie auswerten. null/weglassen = gesamte Historie.'
    ),
  limitMonths: z
    .number()
    .int()
    .nullable()
    .optional()
    .describe(
      'Nur Commits der letzten N Monate auswerten. null/weglassen = gesamte Historie.'
    ),
} as const;
type LimitsArgs = {
  limitCommits?: number | null;
  limitMonths?: number | null;
};

function toLimits(input: Partial<z.infer<typeof limitsSchema>>): Limits {
  return {
    limitCommits:
      typeof input.limitCommits === 'number' ? input.limitCommits : null,
    limitMonths:
      typeof input.limitMonths === 'number' ? input.limitMonths : null,
  };
}

/**
 * Wraps a result so it is returned both as JSON text (backwards compatible) and
 * as `structuredContent` (validated by the tool's `outputSchema`).
 */
function jsonResult<T>(data: T) {
  return {
    content: [{ type: 'text' as const, text: JSON.stringify(data) }],
    structuredContent: data as Record<string, unknown>,
  };
}

// Shared output building blocks ------------------------------------------------

type FolderShape = { name: string; path: string; folders: FolderShape[] };
const folderSchema: z.ZodType<FolderShape> = z.lazy(() =>
  z.object({
    name: z.string().describe('Ordnername'),
    path: z.string().describe('Vollstaendiger Pfad des Ordners im Repo'),
    folders: z.array(folderSchema).describe('Unterordner'),
  })
) as unknown as z.ZodType<FolderShape>;

const commitSchema = z.object({
  commitHash: z.string().describe('Gekuerzter Commit-Hash (8 Zeichen)'),
  date: z.string().describe('Commit-Datum (ISO-8601)'),
  author: z.string().describe('Autor des Commits'),
  message: z.string().describe('Commit-Nachricht'),
  linesAdded: z.number().describe('Hinzugefuegte Zeilen in diesem Commit'),
  linesRemoved: z.number().describe('Entfernte Zeilen in diesem Commit'),
  totalLines: z
    .number()
    .describe('Gesamtzeilen der Datei zum Commit-Zeitpunkt'),
  complexity: z
    .number()
    .describe('McCabe-Komplexitaet der Datei zum Commit-Zeitpunkt'),
});

const configObjectSchema = z.object({
  scopes: z
    .array(z.string())
    .describe('Modul-/Ordner-Praefixe, die je eine Domaene/ein Feature bilden'),
  groups: z
    .array(z.string())
    .describe('Gruppen-Labels zur Bündelung von Scopes'),
  entries: z
    .array(z.unknown())
    .describe('Zusaetzliche, frei definierte Eintraege'),
  filter: z
    .object({
      files: z
        .array(z.string())
        .describe(
          'Glob-/Pfadmuster, die von der Datei-Analyse ausgeschlossen werden'
        ),
      logs: z
        .array(z.string())
        .describe(
          'Muster, die aus der Git-Log-Auswertung ausgeschlossen werden'
        ),
    })
    .describe('Filter fuer Dateien und Git-Logs'),
  aliases: z
    .record(z.string(), z.string())
    .describe('Abbildung von Autorennamen auf einen kanonischen Namen'),
  teams: z
    .record(z.string(), z.array(z.string()))
    .describe('Team-Name -> Liste der zugehoerigen Personen'),
});

export function createMcpServer(options: Options): McpServer {
  const server = new McpServer(
    {
      name: 'detective-backend',
      version: DETECTIVE_VERSION,
    },
    { instructions: DETECTIVE_MCP_INSTRUCTIONS }
  );

  // config_read
  server.registerTool(
    'config_read',
    {
      title: 'Detective-Konfiguration lesen',
      description:
        'Liest die Detective-Konfiguration (Scopes/Domaenen, Gruppen, Teams, Aliasse, Filter). Nutze dies, um die vorhandenen Scopes und die Projekt-Struktur zu verstehen, bevor du Architektur-Analysen interpretierst.',
      inputSchema: {},
      outputSchema: configObjectSchema.shape,
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async () => {
      const cfg = loadConfig(options);
      return jsonResult(cfg);
    }
  );

  // config_write
  server.registerTool(
    'config_write',
    {
      title: 'Detective-Konfiguration schreiben',
      description:
        'Ueberschreibt die Detective-Konfigurationsdatei vollstaendig mit dem uebergebenen Objekt. Achtung: ersetzt die bestehende Konfiguration (Scopes, Teams etc.). Vorher ggf. config_read aufrufen und das Objekt vollstaendig mitgeben.',
      inputSchema: {
        config: configObjectSchema.describe(
          'Vollstaendige Konfiguration, die persistiert werden soll'
        ),
      },
      outputSchema: {
        ok: z.boolean().describe('true, wenn gespeichert wurde'),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
      },
    },
    async ({ config }: { config?: unknown }) => {
      if (!config || typeof config !== 'object') {
        throw new Error("'config' object is required");
      }
      const path = await import('node:path');
      const fs = await import('node:fs/promises');
      const filePath = path.join(process.cwd(), options.config);
      await fs.writeFile(filePath, JSON.stringify(config, null, 2), 'utf8');
      return jsonResult({ ok: true });
    }
  );

  // cache_status
  server.registerTool(
    'cache_status',
    {
      title: 'Status des Log-Caches',
      description:
        'Prueft, ob der zwischengespeicherte Git-Log veraltet ist. Vor historienbasierten Analysen (changeCoupling_get, teamAlignment_get, hotspots_*, trendAnalysis_run) sinnvoll, um zu entscheiden, ob cache_update noetig ist.',
      inputSchema: {},
      outputSchema: {
        isStale: z
          .boolean()
          .describe('true, wenn der Git-Log-Cache neu aufgebaut werden sollte'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async () => {
      const stale = isStale();
      return jsonResult({ isStale: stale });
    }
  );

  // cache_update
  server.registerTool(
    'cache_update',
    {
      title: 'Log-Cache aktualisieren',
      description:
        'Baut den Git-Log-Cache neu auf. Aufrufen, wenn cache_status meldet, dass der Cache veraltet ist, oder nach neuen Commits, bevor historienbasierte Analysen laufen.',
      inputSchema: {},
      outputSchema: {
        ok: z.boolean().describe('true, wenn der Cache aktualisiert wurde'),
      },
      annotations: { readOnlyHint: false, idempotentHint: true },
    },
    async () => {
      await updateLogCache();
      return jsonResult({ ok: true });
    }
  );

  // modules_get
  server.registerTool(
    'modules_get',
    {
      title: 'Modul-/Scope-Groessen',
      description:
        'Liefert die Anzahl Dateien je Scope/Domaene. Nutze dies, um die Groesse und Balance der Domaenen-Schnitte zu beurteilen (sehr grosse oder sehr kleine Scopes deuten auf einen suboptimalen Schnitt hin).',
      inputSchema: {},
      outputSchema: {
        fileCount: z
          .array(z.number())
          .describe(
            'Anzahl Dateien je Scope, in derselben Reihenfolge wie config.scopes (alphabetisch sortiert).'
          ),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async () => {
      const result = calcModuleInfo(options);
      return jsonResult(result);
    }
  );

  // folders_get
  server.registerTool(
    'folders_get',
    {
      title: 'Ordnerstruktur ableiten',
      description:
        'Leitet die hierarchische Ordnerstruktur aus den im Code gefundenen Abhaengigkeiten ab. Nutze dies, um die Projektstruktur zu verstehen und Kandidaten fuer Scopes/Domaenen zu identifizieren.',
      inputSchema: {},
      outputSchema: {
        folders: z
          .array(folderSchema)
          .describe('Top-Level-Ordner mit verschachtelten Unterordnern'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async () => {
      const result = inferFolders(options);
      return jsonResult({ folders: result });
    }
  );

  // coupling_get
  server.registerTool(
    'coupling_get',
    {
      title: 'Strukturelle Kopplung & Kohaesion',
      description:
        'Berechnet die strukturelle Kopplungsmatrix (Imports zwischen Scopes) und die Kohaesion je Scope. Primaeres Werkzeug, um Domaenen-Schnitte / Modulgrenzen / Bounded Contexts zu bewerten: hohe Kohaesion und geringe Kopplung zwischen Scopes sprechen fuer einen guten Schnitt; viele Eintraege ausserhalb der Diagonale deuten auf zu enge Kopplung der Domaenen hin.',
      inputSchema: {},
      outputSchema: {
        groups: z.array(z.string()).describe('Gruppen-Labels der Scopes'),
        dimensions: z
          .array(z.string())
          .describe(
            'Scope-Namen; zugleich Zeilen-/Spaltenbeschriftung der Matrix'
          ),
        fileCount: z
          .array(z.number())
          .describe('Anzahl Dateien je Scope (Reihenfolge wie dimensions)'),
        cohesion: z
          .array(z.number())
          .describe(
            'Kohaesion je Scope in Prozent: Anteil interner Importe an den theoretisch moeglichen internen Verbindungen.'
          ),
        matrix: z
          .array(z.array(z.number()))
          .describe(
            'Quadratische Matrix: matrix[i][j] = Anzahl Imports von Scope i nach Scope j. Diagonale (i==j) = interne Kopplung des Scopes.'
          ),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async () => {
      const result = calcCoupling(options);
      return jsonResult(result);
    }
  );

  // changeCoupling_get
  server.registerTool(
    'changeCoupling_get',
    {
      title: 'Logische (zeitliche) Kopplung',
      description:
        'Berechnet die Change Coupling: wie oft Scopes gemeinsam im selben Commit geaendert werden (logische/zeitliche Kopplung aus der Git-Historie). Ergaenzt coupling_get bei der Bewertung von Domaenen-Schnitten: Scopes, die staendig zusammen geaendert werden, gehoeren fachlich evtl. zusammen oder sind schlecht geschnitten.',
      inputSchema: limitsSchemaShape,
      outputSchema: {
        dimensions: z
          .array(z.string())
          .describe('Modul-/Scope-Namen; Achsenbeschriftung der Matrix'),
        groups: z.array(z.string()).describe('Gruppen-Labels der Scopes'),
        matrix: z
          .array(z.array(z.number()))
          .describe(
            'matrix[i][j] = Anzahl Commits, in denen Modul i und Modul j gemeinsam geaendert wurden (nur obere Dreiecksmatrix gefuellt).'
          ),
        sumOfCoupling: z
          .array(z.number())
          .describe('Summe der Co-Change-Beziehungen je Modul'),
        fileCount: z
          .array(z.number())
          .describe('Anzahl Commits, die das jeweilige Modul beruehrt haben'),
        cohesion: z
          .array(z.number())
          .describe('Reserviert; aktuell -1 (nicht berechnet) je Modul'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async (input: LimitsArgs) => {
      const result = await calcChangeCoupling(toLimits(input), options);
      return jsonResult(result);
    }
  );

  // teamAlignment_get
  server.registerTool(
    'teamAlignment_get',
    {
      title: 'Team-Alignment (Conway)',
      description:
        'Ermittelt, welche Teams (oder Personen) wie viele Zeilen in welchen Modulen geaendert haben. Nutze dies fuer eine Conway-Bewertung der Domaenen-Schnitte: idealerweise arbeitet ein Team primaer in seinem Modul. Streuen viele Teams ueber ein Modul, ist der Schnitt evtl. ungeeignet.',
      inputSchema: {
        ...limitsSchemaShape,
        byUser: z
          .boolean()
          .optional()
          .describe(
            'true = Aufschluesselung pro Person statt pro Team. Default: false (pro Team).'
          ),
      },
      outputSchema: {
        modules: z
          .record(
            z.string(),
            z.object({
              changes: z
                .record(z.string(), z.number())
                .describe(
                  'Team- bzw. Personenname -> Summe geaenderter Zeilen (added + removed) in diesem Modul'
                ),
            })
          )
          .describe('Modulname -> Aenderungsverteilung'),
        teams: z
          .array(z.string())
          .describe('Liste der vorkommenden Teams bzw. Personen'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async (input: LimitsArgs & { byUser?: boolean }) => {
      const { byUser = false, ...rest } = input as LimitsArgs & {
        byUser?: boolean;
      };
      const result = await calcTeamAlignment(byUser, toLimits(rest), options);
      return jsonResult(result);
    }
  );

  // hotspots_find
  server.registerTool(
    'hotspots_find',
    {
      title: 'Hotspot-Dateien finden',
      description:
        'Findet Hotspot-Dateien: Dateien mit hoher Komplexitaet und hoher Aenderungsfrequenz (Score = Komplexitaet * Commits). Nutze dies, um konkrete Refactoring-Kandidaten / technische Schulden auf Dateiebene zu identifizieren; das Ergebnis ist absteigend nach Score sortiert.',
      inputSchema: {
        ...limitsSchemaShape,
        module: z
          .string()
          .default('')
          .describe(
            'Auf einen Modul-/Ordner-Praefix einschraenken (z.B. "src/app/booking"). Leer = ganzes Repo.'
          ),
        minScore: z
          .number()
          .default(-1)
          .describe(
            'Mindest-Score (Komplexitaet * Commits). Dateien darunter werden weggefiltert. -1 = kein Filter.'
          ),
        metric: z
          .enum(['McCabe', 'Length'])
          .default('McCabe')
          .describe(
            'Komplexitaetsmass: "McCabe" = zyklomatische Komplexitaet (nur .ts), "Length" = Anzahl Zeilen.'
          ),
      },
      outputSchema: {
        hotspots: z
          .array(
            z.object({
              fileName: z.string().describe('Repo-relativer Pfad der Datei'),
              commits: z
                .number()
                .describe('Anzahl Commits, die die Datei geaendert haben'),
              changedLines: z
                .number()
                .describe('Summe geaenderter Zeilen (added + removed)'),
              complexity: z
                .number()
                .describe(
                  'Komplexitaet gemaess gewaehlter metric (-1 = unbekannt)'
                ),
              score: z
                .number()
                .describe(
                  'Hotspot-Score = complexity * commits (-1 = unbekannt)'
                ),
            })
          )
          .describe('Hotspots, absteigend nach score sortiert'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async (
      input: LimitsArgs & {
        module?: string;
        minScore?: number;
        metric?: ComplexityMetric;
      }
    ) => {
      const criteria: HotspotCriteria = {
        module: input.module ?? '',
        minScore: typeof input.minScore === 'number' ? input.minScore : -1,
        metric: (input.metric as ComplexityMetric) ?? 'McCabe',
      };
      const limits = toLimits(input);
      const result = await findHotspotFiles(criteria, limits, options);
      return jsonResult(result);
    }
  );

  // hotspots_aggregate
  server.registerTool(
    'hotspots_aggregate',
    {
      title: 'Hotspots je Modul aggregieren',
      description:
        'Aggregiert Hotspot-Statistiken pro Modul: zaehlt je Modul, wie viele Dateien als ok, Warnung oder Hotspot eingestuft sind (Grenzen relativ zum Maximal-Score). Nutze dies fuer einen Ueberblick, welche Module die meisten problematischen Dateien enthalten.',
      inputSchema: {
        ...limitsSchemaShape,
        minScore: z
          .number()
          .default(50)
          .describe(
            'Prozentwert (0-100) des Maximal-Scores; legt die Warning-Grenze fest. Die Hotspot-Grenze liegt in der Mitte zwischen Warning-Grenze und Maximal-Score.'
          ),
        metric: z
          .enum(['McCabe', 'Length'])
          .default('McCabe')
          .describe(
            'Komplexitaetsmass: "McCabe" = zyklomatische Komplexitaet (nur .ts), "Length" = Anzahl Zeilen.'
          ),
      },
      outputSchema: {
        aggregated: z
          .array(
            z.object({
              parent: z.string().describe('Uebergeordneter Pfad des Moduls'),
              module: z.string().describe('Anzeigename des Moduls'),
              count: z
                .number()
                .describe('Anzahl ok-eingestufter Dateien (== countOk)'),
              countOk: z
                .number()
                .describe('Dateien unterhalb der Warning-Grenze'),
              countWarning: z
                .number()
                .describe('Dateien zwischen Warning- und Hotspot-Grenze'),
              countHotspot: z
                .number()
                .describe('Dateien ab der Hotspot-Grenze'),
            })
          )
          .describe('Pro Modul aggregiert, absteigend nach count sortiert'),
        minScore: z.number().describe('Niedrigster vorkommender Score'),
        maxScore: z.number().describe('Hoechster vorkommender Score'),
        warningBoundary: z.number().describe('Score-Grenze fuer "Warnung"'),
        hotspotBoundary: z.number().describe('Score-Grenze fuer "Hotspot"'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async (
      input: LimitsArgs & { minScore?: number; metric?: ComplexityMetric }
    ) => {
      const criteria: HotspotCriteria = {
        module: '',
        minScore: typeof input.minScore === 'number' ? input.minScore : 50,
        metric: (input.metric as ComplexityMetric) ?? 'McCabe',
      };
      const limits = toLimits(input);
      const result = await aggregateHotspots(criteria, limits, options);
      return jsonResult(result);
    }
  );

  // trendAnalysis_run
  server.registerTool(
    'trendAnalysis_run',
    {
      title: 'Trendanalyse ausfuehren',
      description:
        'Analysiert die Entwicklung von Komplexitaet und Groesse der Dateien ueber die letzten Commits. Nutze dies, um zu verstehen, wie sich die Codequalitaet ueber die Zeit verändert und welche Dateien zunehmend komplexer werden. Kann bei grossen Repos/vielen Commits laufzeitintensiv sein.',
      inputSchema: {
        maxCommits: z
          .number()
          .int()
          .default(50)
          .describe('Anzahl der juengsten Commits, die analysiert werden.'),
        parallelWorkers: z
          .number()
          .int()
          .min(1)
          .max(10)
          .default(5)
          .describe('Anzahl paralleler Worker (1-10) fuer die Analyse.'),
        fileExtensions: z
          .array(z.string())
          .default(['.ts', '.js', '.tsx', '.jsx'])
          .describe('Dateiendungen, die beruecksichtigt werden.'),
      },
      outputSchema: {
        files: z
          .array(
            z.object({
              filePath: z.string().describe('Repo-relativer Pfad der Datei'),
              changeFrequency: z
                .number()
                .describe(
                  'Anzahl analysierter Commits, die die Datei aenderten'
                ),
              averageComplexity: z
                .number()
                .describe(
                  'Durchschnittliche McCabe-Komplexitaet ueber die Commits'
                ),
              averageSize: z
                .number()
                .describe('Durchschnittliche Dateigroesse (Zeilen)'),
              totalChanges: z
                .number()
                .describe('Summe aller geaenderten Zeilen (added + removed)'),
              commits: z
                .array(commitSchema)
                .describe('Einzelne Commits, die die Datei betrafen'),
              complexityTrend: z
                .array(
                  z.object({
                    commit: z.string().describe('Gekuerzter Commit-Hash'),
                    date: z.string().describe('Datum (ISO-8601)'),
                    complexity: z
                      .number()
                      .describe('Komplexitaet zu diesem Commit'),
                  })
                )
                .describe('Komplexitaetsverlauf ueber die Commits'),
              sizeTrend: z
                .array(
                  z.object({
                    commit: z.string().describe('Gekuerzter Commit-Hash'),
                    date: z.string().describe('Datum (ISO-8601)'),
                    lines: z.number().describe('Zeilenanzahl zu diesem Commit'),
                  })
                )
                .describe('Groessenverlauf ueber die Commits'),
            })
          )
          .describe('Pro aktuell existierender Datei die Trenddaten'),
        summary: z
          .object({
            totalProcessingTimeMs: z
              .number()
              .describe('Gesamtdauer der Analyse in Millisekunden'),
            commitsAnalyzed: z.number().describe('Anzahl analysierter Commits'),
            filesAnalyzed: z.number().describe('Anzahl analysierter Dateien'),
            commitHashes: z
              .array(z.string())
              .describe('Liste der (gekuerzten) analysierten Commit-Hashes'),
            timingMetrics: z
              .record(z.string(), z.number())
              .describe('Detaillierte Laufzeitmetriken in Millisekunden'),
          })
          .describe('Zusammenfassung des Analyselaufs'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async (args: {
      maxCommits?: number;
      parallelWorkers?: number;
      fileExtensions?: string[];
    }) => {
      const maxCommits = args.maxCommits ?? 50;
      const parallelWorkers = Math.max(
        1,
        Math.min(10, args.parallelWorkers ?? 5)
      );
      const fileExtensions = args.fileExtensions ?? [
        '.ts',
        '.js',
        '.tsx',
        '.jsx',
      ];
      const result = await runTrendAnalysis(options, {
        maxCommits,
        fileExtensions,
        parallelWorkers,
      });
      const formatted = await formatTrendAnalysisForAPI(
        result,
        options.path,
        fileExtensions
      );
      return jsonResult(formatted);
    }
  );

  // xray_get
  server.registerTool(
    'xray_get',
    {
      title: 'X-Ray einer Datei',
      description:
        'Fuehrt eine tiefe Code-Analyse einer einzelnen Datei durch (Methoden-, Klassen-, Datenstruktur-, Organisations- und TypeScript-Metriken). Nutze dies, um eine konkrete (z.B. zuvor als Hotspot identifizierte) Datei im Detail zu bewerten. Die genaue Struktur der Metriken liefert xray_schema.',
      inputSchema: {
        file: z
          .string()
          .describe(
            'Repo-relativer Pfad der zu analysierenden Datei (muss innerhalb des Repos liegen).'
          ),
        includeSource: z
          .boolean()
          .default(false)
          .describe('true = Quelltext der Datei in die Antwort aufnehmen.'),
      },
      outputSchema: {
        file: z.string().describe('Analysierter Dateipfad'),
        metrics: z
          .record(z.string(), z.unknown())
          .describe(
            'Nach Kategorien gruppierte Metriken (dynamisch; Schema siehe xray_schema).'
          ),
        sourceCode: z
          .string()
          .optional()
          .describe('Quelltext der Datei (nur wenn includeSource = true).'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async ({
      file,
      includeSource,
    }: {
      file?: string;
      includeSource?: boolean;
    }) => {
      if (!file) {
        throw new Error("Parameter 'file' is required");
      }
      const path = await import('node:path');
      const fs = await import('node:fs');
      const repoRoot = path.resolve(options.path);
      const fullPath = path.resolve(repoRoot, file);
      const relativeToRoot = path.relative(repoRoot, fullPath);
      if (relativeToRoot.startsWith('..') || path.isAbsolute(relativeToRoot)) {
        throw new Error('File path must be within the repository root');
      }
      if (!fs.existsSync(fullPath)) {
        throw new Error(`File not found: ${file}`);
      }
      const { CodeAnalyzer } = await import(
        '../services/trend-analysis/x-ray/code-analyzer'
      );
      const analyzer = new CodeAnalyzer(fullPath);
      const metrics = await analyzer.analyze(includeSource ?? false);
      return jsonResult(metrics);
    }
  );

  // xray_schema
  server.registerTool(
    'xray_schema',
    {
      title: 'X-Ray-Schema',
      description:
        'Liefert das JSON-Schema und das UI-Schema fuer die X-Ray-Metriken. Nutze dies, um die von xray_get gelieferten (dynamischen) Metrikfelder zu verstehen und korrekt zu interpretieren.',
      inputSchema: {},
      outputSchema: {
        version: z.number().describe('Schema-Version'),
        jsonSchema: z
          .record(z.string(), z.unknown())
          .describe('JSON-Schema der X-Ray-Metriken'),
        uiSchema: z
          .record(z.string(), z.unknown())
          .describe('UI-Schema zur Darstellung der Metriken'),
      },
      annotations: { readOnlyHint: true, idempotentHint: true },
    },
    async () => {
      const { CodeAnalyzer } = await import(
        '../services/trend-analysis/x-ray/code-analyzer'
      );
      const { buildBaseXRaySchema } = await import(
        '../services/trend-analysis/x-ray/x-ray.schema'
      );
      const jsonSchema = CodeAnalyzer.buildJSONSchema() as Record<
        string,
        unknown
      >;
      const uiSchema =
        (jsonSchema as { [key: string]: unknown })['x-ui'] ??
        CodeAnalyzer.buildUISchema();
      const base = buildBaseXRaySchema();
      return jsonResult({
        version: base.version,
        jsonSchema,
        uiSchema,
      });
    }
  );

  return server;
}
