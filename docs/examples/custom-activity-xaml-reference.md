# Referenz: Codeaktivität mit Lookup-Eingaben (vom Designer erzeugt)

Vorlage aus `contoso-dev`, Workflow **ZZ Inspect CustomActivity** (`7b74d4c2-e18b-f111-8076-7c1e52217f40`),
Schritt im Designer konfiguriert: `msdyncrmWorkflowTools.CheckUserInTeam` mit `Team` = Salesteam
(fester Wert) und `User` = Besitzer der verknüpften Firma.

## Sollzustand

```xml
<!-- Workflow-Ebene: Typ und Default entsprechen dem PARAMETERTYP -->
<mxswa:Workflow.Variables>
  <Variable x:TypeArguments="x:Boolean" Default="False"
            Name="CustomActivityStep1isUserInTeam_localParameter" />
</mxswa:Workflow.Variables>

<mxswa:ActivityReference AssemblyQualifiedName="…Activities.Composite, …"
                        DisplayName="CustomActivityStep1: Ist der Besiter der Kunde-Firma im Salesteam?">
  <mxswa:ActivityReference.Properties>
    <!-- Hilfsvariablen liegen IM Composite -->
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables">
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_1" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_2" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_1_converted" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_3" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_4" />
      <Variable x:TypeArguments="x:Object" Name="CustomActivityStep1_3_converted" />
    </sco:Collection>
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">

      <!-- (1) Fester Lookup: Guid mit Marker "UniqueIdentifier", TargetType mxs:EntityReference -->
      … EvaluateExpression: CreateCrmType,
        Parameters = [New Object() { …WorkflowPropertyType.Guid, "a0000001-0000-4000-8000-000000000001", "UniqueIdentifier" }],
        TargetType = mxs:EntityReference, Result = [CustomActivityStep1_2]

      <!-- (2) daraus die Referenz: Label ist LEER -->
      … EvaluateExpression: CreateCrmType,
        Parameters = [New Object() { …WorkflowPropertyType.EntityReference, "team", "", CustomActivityStep1_2, "Lookup" }],
        TargetType = mxs:EntityReference, Result = [CustomActivityStep1_1]

      <!-- (3) für das Argument in .NET-Typ wandeln -->
      … ConvertCrmXrmTypes: Value=[CustomActivityStep1_1], TargetType=mxs:EntityReference
        → [CustomActivityStep1_1_converted]

      <!-- (4) verknüpftes Feld lesen; TargetType ist der ZIELTYP, nicht null -->
      <mxswa:GetEntityProperty Attribute="ownerid"
          Entity='[InputEntities("related_sample_kundefirma#account")]' EntityName="account"
          Value="[CustomActivityStep1_4]">
        <mxswa:GetEntityProperty.TargetType>
          <InArgument x:TypeArguments="s:Type">
            <mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="mxs:EntityReference" />
          </InArgument>
        </mxswa:GetEntityProperty.TargetType>
      </mxswa:GetEntityProperty>

      <!-- (5) SelectFirstNonNull + Convert wie gewohnt → [CustomActivityStep1_3_converted] -->

      <!-- (6) die Aktivität selbst: Argumenttypen = Parametertypen -->
      <mxswa:ActivityReference
          AssemblyQualifiedName="msdyncrmWorkflowTools.CheckUserInTeam, msdyncrmWorkflowTools, Version=1.0.62.1, Culture=neutral, PublicKeyToken=416e876b9bee261e"
          DisplayName="CustomActivityStep1: Ist der Besiter der Kunde-Firma im Salesteam?">
        <mxswa:ActivityReference.Arguments>
          <InArgument  x:TypeArguments="mxs:EntityReference" x:Key="Team">[DirectCast(CustomActivityStep1_1_converted, Microsoft.Xrm.Sdk.EntityReference)]</InArgument>
          <InArgument  x:TypeArguments="mxs:EntityReference" x:Key="User">[DirectCast(CustomActivityStep1_3_converted, Microsoft.Xrm.Sdk.EntityReference)]</InArgument>
          <OutArgument x:TypeArguments="x:Boolean" x:Key="isUserInTeam">[CustomActivityStep1isUserInTeam_localParameter]</OutArgument>
        </mxswa:ActivityReference.Arguments>
      </mxswa:ActivityReference>
    </sco:Collection>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

## Behobene Abweichungen des Builders

Alle fünf sind umgesetzt; die Merkmalsmatrix in `WorkflowActivationProbeTests` aktiviert die
Codeaktivität mit Lookup-Literal, mit allen Parametern und innerhalb eines Zweigs.

| # | Soll | War vorher | Wirkung |
|---|---|---|---|
| 1 | Ausgabevariable mit dem **Parametertyp** (`x:Boolean`, `Default="False"`) | hart `x:String`, `Default="[Nothing]"` | Typkonflikt |
| 2 | `OutArgument x:TypeArguments` = **Parametertyp** | hart `x:String` | Typkonflikt |
| 3 | Guid-Literal-Marker `"UniqueIdentifier"` | `"Key"` | ungültiger Property-Bag |
| 4 | EntityReference-Label **leer** | Anzeigename | unkritisch, aber abweichend |
| 5 | `GetEntityProperty.TargetType` = Zieltyp (`mxs:EntityReference`) | bei Bedingungen `x:Null`, bei Werten Zieltyp | war für Lookup-Werte bereits korrekt |

Richtig war dagegen: Hilfsvariablen gehören **in** die `Variables`-Collection des Composite.

## Die eigentliche Ursache: das Namensschema der Hilfsvariablen

Auch mit den korrekten Typen blieb `InvalidPropertyBag` bestehen, während dasselbe XAML aus dem
Designer aktivierte. Der einzige verbleibende Unterschied war die **Benennung der konvertierten
Variable**:

```
Designer:      _1, _2, _1_converted          ← zu _1_converted existiert _1
Builder (alt): _1, _2, _3_converted          ← _3 gibt es nicht
```

`Convert` hatte einen neuen Index verbraucht, statt an die Quellvariable anzuhängen. Die Aktivierung
rekonstruiert den Schritt aus genau diesen Namen — ein `_3_converted` ohne deklariertes `_3` ist für
sie ein kaputter Property-Bag. Dazu passend: der Designer reserviert erst den **Ergebnis**-Slot und
dann die Quelle (`_1` = Referenz, `_2` = Guid), nicht umgekehrt.

> [!IMPORTANT]
> Das ist derselbe Mechanismus wie bei `0x80045037`: Namen sind hier Struktur. Ein Selbsttest, der
> nur „ist jede referenzierte Variable deklariert?" prüft, fängt das nicht — hier war die
> *unbenutzte* Basisvariable das Problem, nicht eine fehlende Referenz.

## Weitere Stellen, an denen der Marker bzw. der Typname zählt

Dieselbe Klasse von Fehlern — das XAML sieht plausibel aus und wird mit `0x80045040` schon beim
Schreiben abgelehnt:

| Konstrukt | Falsch | Richtig |
|---|---|---|
| `CreateCrmType` für ein Optionsset | Marker `"OptionSetValue"` | Marker **`"Picklist"`** |
| `CreateCrmType` für eine Guid | Marker `"Guid"` | Marker **`"UniqueIdentifier"`** |
| `CreateCrmType` für eine Referenz | Marker `"EntityReference"` | Marker **`"Lookup"`** (5-teilig) |
| Datums-Typargument | `x:DateTime` | **`s:DateTime`** — der XAML-2006-Namespace hat kein DateTime |
| `RetrieveCurrentTime` | `TargetType` = Zieltyp | **`x:Null`**, Parameter `[New Object() {  }]` mit `xml:space="preserve"` |
| `Add` (Verkettung) | `TargetType` = Zieltyp | **`x:Null`** — die Teile bestimmen den Typ |

Der Marker ist also der **CRM-Attributtyp**, nicht der Name des `WorkflowPropertyType`. Belegt durch
`ValueKindWriteProbeTests`, das jede Wertform einzeln schreibt.

Dazu zwei Dinge, die erst die Aktivierung des ganzen Workflows zeigte — beide `0x80040216`:

| Konstrukt | Falsch | Richtig |
|---|---|---|
| Komma in einer Konstante | `"1,2,3"` | **`"1&#44;2&#44;3"`** — das Parameter-Array wird vor den String-Literalen auf Kommas zerlegt |
| Persistenzpunkt in einem Realtime-Workflow | `<Persist />` | **entfällt** — nur Hintergrund-Workflows dürfen persistieren |
| `fromStep`-Verweis nach dem Zurücklesen | die alte Schritt-Id | über die **Entität** auflösen — Neunummerierung macht die Id ungültig, der Verweis zeigt dann auf einen nie erzeugten Datensatz |

Der letzte Punkt war der zäheste: das XAML war wohlgeformt, der Selbsttest zufrieden, die Validierung
still. Gefunden durch Halbieren — `PaymentReminderBisectTests` schreibt Teilmengen des Workflows und
aktiviert jede einzeln, bis nur noch die drei Codeaktivitäten mit dem `fromStep`-Verweis übrig waren.

## Umgesetzte Validierung

Der Parametertyp wird aus `plugintype.customworkflowactivityinfo` gelesen
(`WorkflowActivityCatalog`) und vor dem PATCH gegen das Modell geprüft:

| Prüfung | Code |
|---|---|
| Existiert der Parametername (`DependencyPropertyName`)? | `WF085` (Eingabe), `WF089` (Ausgabe) |
| Passt der `dataType` zum `TypeName` des Parameters? | `WF086` |
| Sind alle mit `Required=true` markierten Eingaben belegt? | `WF087` |
| Liegt die Zielentität eines Lookups in `EntityNames`? | `WF088` |

Ohne erreichbare Metadaten (Offline-Nutzung, unbekannte Aktivität) entfallen die vier Prüfungen —
dann meldet erst die Aktivierung den Fehler, wie vorher.
