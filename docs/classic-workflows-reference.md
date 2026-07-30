# Klassische Workflows in Microsoft Dataverse: Architektur- und Formatreferenz

> [!IMPORTANT]
> **Dies ist kein offizielles Microsoft-Dokument.** Diese Referenz wurde durch systematisches
> Reverse Engineering des klassischen Prozess-Designers auf einer Dataverse-Umgebung erstellt
> (Netzwerk-Mitschnitt, XAML-Differenzanalyse, Web-API-Abfragen). Sie beschreibt **undokumentierte
> interne Schnittstellen**, die Microsoft ohne Vorankündigung ändern kann. Die beschriebenen
> SOAP-Endpunkte sind nicht für Drittanbieter freigegeben. Verwendung auf eigenes Risiko.
>
> Erhebungsgrundlage: Dataverse 9.2 (`Microsoft.Crm.Workflow` 9.0.0.0), Erhebungsdatum 2026-07-29.

## In diesem Artikel

- [Übersicht](#übersicht)
- [Datenmodell](#datenmodell)
- [Architektur des Prozess-Designers](#architektur-des-prozess-designers)
- [Der Workflow-Webservice](#der-workflow-webservice)
- [Das Bedingungsformat conditionXml](#das-bedingungsformat-conditionxml)
- [Aufbau des Workflow-XAML](#aufbau-des-workflow-xaml)
- [Namenskonventionen](#namenskonventionen)
- [Aktivitätsreferenz](#aktivitätsreferenz)
- [Wertausdrücke](#wertausdrücke)
- [Benutzerdefinierte Workflowaktivitäten](#benutzerdefinierte-workflowaktivitäten)
- [Aktivierung und Kompilierung](#aktivierung-und-kompilierung)
- [Programmatischer Zugriff](#programmatischer-zugriff)
- [Fehlerreferenz](#fehlerreferenz)
- [Einschränkungen und Hinweise](#einschränkungen-und-hinweise)
- [Anhang: Untersuchungsmethodik](#anhang-untersuchungsmethodik)

## Übersicht

Klassische Workflows (in der Benutzeroberfläche „Prozesse" der Kategorie *Workflow*) sind die
Vorgänger der modernen Cloud Flows. Sie werden in der Tabelle `workflow` gespeichert und tragen
ihre Ablauflogik als **Windows Workflow Foundation 4 (WF4) XAML** in der Spalte `xaml`.

Zum Verständnis der Programmierbarkeit sind drei Aussagen zentral:

1. Der klassische Prozess-Designer ist **serverseitig gerendert**. Er erzeugt das XAML nicht im
   Browser, sondern ruft für jede Bearbeitungsaktion einen internen SOAP-Dienst auf, der das XAML
   in der Datenbank fortschreibt und ein HTML-Fragment für die Anzeige zurückliefert.
2. Der Designer **speichert inkrementell**. Es gibt keinen Speichervorgang, der ein im Client
   aufgebautes Modell überträgt. Jeder einzelne Bearbeitungsschritt ist bereits persistiert.
3. Das XAML ist folglich **Ausgabe eines Generators**, nicht Eingabe. Seine Semantik hängt zu
   einem erheblichen Teil an **Namenskonventionen** (Schritt-IDs, `DisplayName`, Variablennamen).
   Wer XAML selbst erzeugt, muss diese Konventionen einhalten, sonst kann der Designer den
   Workflow nicht mehr darstellen.

## Datenmodell

### Tabelle `workflow`

| Spalte | Typ | Beschreibung |
|---|---|---|
| `workflowid` | Uniqueidentifier | Primärschlüssel. Wird serverseitig erzeugt. |
| `name` | String | Anzeigename des Prozesses. |
| `category` | Picklist | `0` = Workflow, `1` = Dialog, `2` = Geschäftsregel, `3` = Aktion, `4` = Geschäftsprozessfluss, `5` = Moderner Fluss. |
| `type` | Picklist | `1` = Definition, `2` = interne Aktivierungskopie, `3` = Vorlage. Abfragen sollten auf `type eq 1` filtern. |
| `primaryentity` | String | Logischer Name der Primärentität. |
| `xaml` | Memo | WF4-XAML der Ablauflogik. Siehe [Aufbau des Workflow-XAML](#aufbau-des-workflow-xaml). |
| `clientdata` | Memo | **Bei klassischen Workflows immer `null`.** Siehe Hinweis unten. |
| `statecode` / `statuscode` | State/Status | `0`/`1` = Entwurf, `1`/`2` = Aktiviert. |
| `mode` | Picklist | `0` = Hintergrund (asynchron), `1` = Echtzeit (synchron). |
| `scope` | Picklist | `1` = Benutzer, `2` = Geschäftseinheit, `3` = Über- und untergeordnete Geschäftseinheiten, `4` = Organisation. |
| `runas` | Picklist | `0` = Besitzer, `1` = Aufrufender Benutzer. |
| `ondemand` | Boolean | Als bedarfsabhängiger Prozess verfügbar. |
| `subprocess` | Boolean | Als untergeordneter Prozess aufrufbar. |
| `triggeroncreate` / `triggerondelete` | Boolean | Auslöser bei Erstellen/Löschen. |
| `createstage` / `updatestage` / `deletestage` | Integer | `20` = Vor dem Vorgang, `40` = Nach dem Vorgang. `0`/`null` = kein Auslöser. |
| `triggeronupdateattributelist` | String | Kommaseparierte Attributliste, die den Aktualisierungs-Auslöser einschränkt. |
| `istransacted`, `asyncautodelete`, `syncworkflowlogonfailure`, `rank` | – | Ausführungsverhalten. |

> [!NOTE]
> `clientdata` wird von klassischen Workflows nicht verwendet — geprüft an mehreren Workflows
> unterschiedlichen Alters, einschließlich frisch im Designer erstellter. Die
> Darstellungsinformationen des Designers stecken vollständig im XAML (siehe
> [Namenskonventionen](#namenskonventionen)). Die Spalte wird von *modernen* Flows genutzt.

### Zugehörige Tabellen

| Tabelle | Verwendung |
|---|---|
| `plugintype` | Registrierte Typen, darunter benutzerdefinierte Workflowaktivitäten. Die Spalte `customworkflowactivityinfo` enthält deren Parametermetadaten. |
| `pluginassembly` | Assemblys mit `publickeytoken`, `culture`, `version`. |

## Architektur des Prozess-Designers

### Einstiegspunkte

Der klassische Designer ist Teil des Legacy-Webclients:

| Zweck | URL |
|---|---|
| Prozessliste (klassisch, **ohne** Befehlsleiste) | `/_root/homepage.aspx?etc=4703` |
| Lösungs-Explorer (klassisch, **mit** Befehlsleiste) | `/tools/solution/edit.aspx?id=%7BFD140AAF-4DF4-11DD-BD17-0019B9312238%7D` |
| Prozess-Designer | `/sfa/workflow/edit.aspx?appSolutionId={solutionId}&id={workflowId}` |
| Bedingungseditor | `/Condition/Condition.aspx?EntityId={workflowId}&StepId={branchStepId}` |
| Feldwert-Editor | `/SFA/Workflow/entityform.aspx?workflowId={id}&entityname={e}&activityname={stepId}&stepId={stepId}&entityFullName={e}&primaryentity={e}&mode=1` |
| Parametereditor für Codeaktivitäten | `/SFA/Workflow/customactivityform.aspx?workflowId={id}&activityname={stepId}&readonlymode=false&customstepcategory=CustomActivity&messageName=` |

> [!TIP]
> Die GUID `{FD140AAF-4DF4-11DD-BD17-0019B9312238}` ist die Standardlösung und in jeder
> Organisation identisch. Die moderne Oberfläche entfernt die Befehlsleiste aus der Prozessliste;
> zum Anlegen eines Prozesses ist der Lösungs-Explorer erforderlich.
>
> Der Aufruf von `/sfa/workflow/edit.aspx` ohne Kontextparameter erzeugt einen Serverfehler.

### Verarbeitungsmodell

Jede Bearbeitungsaktion folgt demselben Muster:

```
Browser ──SOAP──▶ /AppWebServices/Workflow.asmx
                        │
                        ├─▶ ändert workflow.xaml in der Datenbank
                        │
                  ◀─HTML─┘  Fragment für die Designer-Anzeige
```

Die Antwort ist **HTML**, kein XAML. Beispielhaft für das Hinzufügen einer Bedingung:

```html
<div id="WorkflowStep0DIV" style="display:block">
  <table class="ms-crm-workflow-outer" id="ConditionStep1" parent="WorkflowStep0"
         stepname="ConditionStep" tabindex="0" onclick="OnWorkflowStepClick(...)">
```

Das Anzeigemodell des Designers ist damit ein HTML-Baum, dessen Knoten über die Attribute `id`,
`parent` und `stepname` verknüpft sind. Beim erneuten Öffnen rekonstruiert der Server dieses
HTML aus dem gespeicherten XAML.

> [!IMPORTANT]
> Die Schaltfläche **Speichern** im Designer überträgt keine Ablauflogik. Sie pflegt nur
> Metadaten des Prozesses (Name, Auslöser, Bereich). Wurden nur Schritte bearbeitet, erzeugt sie
> keinen Netzwerkaufruf, weil die Änderungen bereits gespeichert sind.

## Der Workflow-Webservice

**Endpunkt:** `POST /AppWebServices/Workflow.asmx`
**Namespace:** `http://schemas.microsoft.com/crm/2009/WebServices`

> [!WARNING]
> **Dieser Dienst ist für API-Aufrufer nicht nutzbar.** Er verlangt das WRPC-Token des
> Legacy-Webclients (Anti-Forgery-Schutz) und antwortet ohne dieses mit
> `soap:Fault … INVALID_WRPC_TOKEN`, selbst bei gültigem Bearer-Token. Die folgende Beschreibung
> dokumentiert das Protokoll des Designers zum Verständnis — zum Schreiben eigener Workflows ist
> die Web API zu verwenden (siehe [Programmatischer Zugriff](#programmatischer-zugriff)).

### CreateWorkflow

Erstellt einen Prozess samt XAML-Grundgerüst.

```xml
<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
  <soap:Body>
    <CreateWorkflow xmlns="http://schemas.microsoft.com/crm/2009/WebServices">
      <workflowName>Beispielprozess</workflowName>
      <primaryEntity>lead</primaryEntity>
      <templateId></templateId>
      <businessProcessType>0</businessProcessType>
      <workflowCategory>0</workflowCategory>
      <isSyncWorkflow>false</isSyncWorkflow>
    </CreateWorkflow>
  </soap:Body>
</soap:Envelope>
```

Antwort:

```xml
<CreateWorkflowResponse><CreateWorkflowResult>e784a882-c8ef-46b2-b6e4-00cbe9146360</CreateWorkflowResult></CreateWorkflowResponse>
```

Nach diesem einen Aufruf existiert der Datensatz vollständig: `category=0`, `type=1`,
`statecode=0`, `statuscode=1` und ein XAML-Grundgerüst von ca. 1770 Zeichen.

### AddCheckStep

Fügt eine Überprüfungsbedingung ein. Erzeugt **zwei** Modellknoten: den Container
(`ConditionStep<N>`) und einen Zweig (`ConditionBranchStep<N+1>`).

```xml
<AddCheckStep xmlns="http://schemas.microsoft.com/crm/2009/WebServices">
  <parentId>WorkflowStep0</parentId>
  <entityId>{E784A882-C8EF-46B2-B6E4-00CBE9146360}</entityId>
  <descriptionXml></descriptionXml>
</AddCheckStep>
```

### UpdateCondition

Setzt die Vergleichslogik eines Bedingungszweigs.

```xml
<UpdateCondition xmlns="http://schemas.microsoft.com/crm/2009/WebServices">
  <activityId>ConditionBranchStep2</activityId>
  <conditionXml><!-- siehe unten, XML-escaped --></conditionXml>
  <entityId>{E784A882-C8EF-46B2-B6E4-00CBE9146360}</entityId>
  <descriptionXml></descriptionXml>
</UpdateCondition>
```

### Weitere Operationen

Die Menüeinträge des Designers tragen stabile Element-IDs nach dem Schema
`mnu_AddStep_<Typ>`; die zugehörigen Dienstoperationen folgen derselben Benennung
(`AddCheckStep` ↔ `mnu_AddStep_CheckStep`).

| Element-ID | Menüeintrag | Erzeugter Schritttyp |
|---|---|---|
| `mnu_AddStep_StageStep` | Phase | `StageStep` |
| `mnu_AddStep_CheckStep` | Überprüfungsbedingung | `ConditionStep` + `ConditionBranchStep` |
| `mnu_AddStep_ElseIfStep` | Bedingungsverzweigung | `ConditionBranchStep` |
| `mnu_AddStep_ElseStep` | Standardaktion | `ConditionBranchStep` |
| `mnu_AddStep_WaitStep` | Wartebedingung | `WaitStep` |
| `mnu_AddStep_WaitBranchStep` | Parallele Warteverzweigung | `WaitBranchStep` |
| `mnu_AddStep_CreateStep` | Datensatz erstellen | `CreateStep` |
| `mnu_AddStep_UpdateStep` | Datensatz aktualisieren | `UpdateStep` |
| `mnu_AddStep_AssignStep` | Datensatz zuweisen | `AssignStep` |
| `mnu_AddStep_SendEmailStep` | E-Mail senden | `SendEmailStep` |
| `mnu_AddStep_ChildWorkflowStep` | Untergeordneten Workflow starten | `ChildWorkflowStep` |
| `mnu_AddStep_SDKOperation` | Aktion durchführen | `InvokeSdkMessageStep` |
| `mnu_AddStep_ChangeStatusStep` | Status ändern | `SetStateStep` |
| `mnu_AddStep_StopWorkflowStep` | Workflow beenden | `StopWorkflowStep` |
| `CustomActivity<pluginTypeId>` | Codeaktivität (Untermenü je Assembly) | `CustomActivityStep` |

## Das Bedingungsformat conditionXml

Bedingungen werden nicht als XAML übertragen, sondern in einem eigenen deklarativen Format, das
der Server in XAML übersetzt.

### Vergleich mit statischem Wert

```xml
<and>
  <condition>
    <column id="colEntity"      value="lead" />
    <column id="colAttribute"   value="lastname"/>
    <column id="colOperator"    value="eq"/>
    <column id="colStaticValue" value="Mustermann" dataslugs="" />
  </condition>
</and>
```

### Vergleich mit Feldwert (Data Slug)

Der Feldverweis wird als `slugbody`-Struktur in das `value`-Attribut eingebettet (dort
XML-escaped) und über `dataslugs` markiert:

```xml
<column id="colStaticValue" dataslugs="0"
        value="<slugbody>
                 <slugelement type=&quot;slug&quot;>
                   <slug type=&quot;dynamic&quot; value=&quot;lead.companyname&quot;/>
                 </slugelement>
               </slugbody>" />
```

`slugbody` kann mehrere `slugelement`-Kinder aufnehmen, wodurch gemischte Ausdrücke aus Text und
Feldverweisen darstellbar sind.

### Vergleichsoperatoren

| Wert | Bedeutung | Wert | Bedeutung |
|---|---|---|---|
| `eq` | gleich | `ne` | ungleich |
| `contains` | enthält | `doesnotcontain` | enthält nicht |
| `beginswith` | beginnt mit | `doesnotbeginwith` | beginnt nicht mit |
| `endswith` | endet mit | `doesnotendwith` | endet nicht mit |
| `not-null` | enthält Daten | `null` | enthält keine Daten |
| `in` | in | `notin` | nicht in |
| `gt` | ist größer als | `ge` | ist größer oder gleich |

## Aufbau des Workflow-XAML

### Dokumentrahmen

Das minimale, vom Server selbst erzeugte Grundgerüst:

```xml
<?xml version="1.0" encoding="utf-16"?>
<Activity x:Class="XrmWorkflow00000000000000000000000000000000"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities" …>
  <x:Members>
    <x:Property Name="InputEntities"   Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
    <x:Property Name="CreatedEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
  </x:Members>
  <this:XrmWorkflow000…0.InputEntities>
    <InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" />
  </this:XrmWorkflow000…0.InputEntities>
  <this:XrmWorkflow000…0.CreatedEntities>
    <InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" />
  </this:XrmWorkflow000…0.CreatedEntities>
  <mva:VisualBasic.Settings>Assembly references and imported namespaces for internal implementation</mva:VisualBasic.Settings>
  <mxswa:Workflow />
</Activity>
```

### Namespacepräfixe

| Präfix | Namespace |
|---|---|
| (Standard) | `http://schemas.microsoft.com/netfx/2009/xaml/activities` |
| `x` | `http://schemas.microsoft.com/winfx/2006/xaml` |
| `this` | `clr-namespace:` |
| `mxs` | `Microsoft.Xrm.Sdk` |
| `mxsq` | `Microsoft.Xrm.Sdk.Query` |
| `mxswa` | `Microsoft.Xrm.Sdk.Workflow.Activities` |
| `mcwa` | `Microsoft.Crm.Workflow.Activities` |
| `mva` | `Microsoft.VisualBasic.Activities` |
| `s`, `scg`, `sco`, `srs` | `System`, `System.Collections.Generic`, `System.Collections.ObjectModel`, `System.Runtime.Serialization` |

> [!WARNING]
> Die Namespacedeklarationen am Wurzelelement werden **bedarfsgesteuert ergänzt**. Das Präfix
> `mcwa` erscheint zum Beispiel erst, wenn der Prozess eine SDK-Nachrichtenaktivität enthält.
> Ein Generator muss die Deklarationen am tatsächlichen Inhalt ausrichten.

### Kontextwörterbücher

| Ausdruck | Bedeutung |
|---|---|
| `[InputEntities("primaryEntity")]` | Der auslösende Datensatz. |
| `[InputEntities("primaryEntity").Id]` | Dessen Primärschlüssel. |
| `[CreatedEntities("<StepId>_localParameter")]` | Der von einem Erstellungsschritt angelegte Datensatz. |
| `[CreatedEntities("<name>#Temp")]` | Temporäre Instanz für Datenänderungen (siehe unten). |

### Änderungsmuster mit temporärer Entität

Alle datenverändernden Schritte folgen demselben Ablauf: neue Instanz erzeugen, Schlüssel
übernehmen, Aktion ausführen, Ergebnis zurückschreiben, Persistenzpunkt setzen.

```xml
<Sequence DisplayName="UpdateStep3">
  <Assign x:TypeArguments="mxs:Entity" To='[CreatedEntities("primaryEntity#Temp")]'    Value='[New Entity("lead")]' />
  <Assign x:TypeArguments="s:Guid"     To='[CreatedEntities("primaryEntity#Temp").Id]' Value='[InputEntities("primaryEntity").Id]' />
  <!-- hier: Wertaufbereitung und SetEntityProperty -->
  <mxswa:UpdateEntity DisplayName="UpdateStep3" Entity='[CreatedEntities("primaryEntity#Temp")]' EntityName="lead" />
  <Assign x:TypeArguments="mxs:Entity" To='[InputEntities("primaryEntity")]' Value='[CreatedEntities("primaryEntity#Temp")]' />
  <Persist />
</Sequence>
```

## Namenskonventionen

> [!IMPORTANT]
> Diese Konventionen sind nicht kosmetisch. Der Designer leitet sein Anzeigemodell aus ihnen ab.
> Abweichungen führen dazu, dass der Prozess nicht mehr geöffnet werden kann (Fehler `0x80045037`).

### Schritt-IDs

Muster `<Typ>Step<N>`. Der Zähler `N` läuft **fortlaufend über alle Schritttypen** eines Prozesses
und wird nicht je Typ zurückgesetzt. Beispiel einer realen Nummerierung:

```
ConditionStep1, ConditionBranchStep2, UpdateStep3, CustomActivityStep4, CreateStep5,
AssignStep6, SendEmailStep7, ChildWorkflowStep8, SetStateStep9, InvokeSdkMessageStep10,
StopWorkflowStep11, WaitStep12, WaitBranchStep13
```

### DisplayName

| Fall | Wert |
|---|---|
| Schritt ohne Beschreibung | `<StepId>`, z. B. `UpdateStep6` |
| Schritt mit Beschreibung | `<StepId>: <Beschreibung>`, z. B. `UpdateStep6: Update Lead.Domain` |
| Innere Aktivität eines Schritts | immer nur `<StepId>` |
| Zweig-Wrapper (`Composite`) | die **Zweig**-ID, nicht die des enthaltenen Schritts |
| Hilfsaktivitäten | fester Text: `EvaluateExpression`, `EvaluateCondition`, `EvaluateLogicalCondition`, `ConvertCrmXrmTypes` |

### Variablen

| Muster | Typ | Verwendung |
|---|---|---|
| `<StepId>_<n>` | `x:Object` | Zwischenwerte. `_1` ist der Ergebnisslot, `_2` … `_n` die Quellen in Auswertungsreihenfolge. |
| `<StepId>_condition` | `x:Boolean`, `Default="False"` | Ergebnis einer Bedingungsauswertung. |
| `<StepId>_<n>_converted` | `x:Object` | Nach Typkonvertierung für Argumente von Codeaktivitäten. |
| `<StepId><ParameterName>_localParameter` | Parametertyp, `Default="[Nothing]"` | Ein-/Ausgabeparameter einer Codeaktivität. **Auf Workflowebene** in `<mxswa:Workflow.Variables>` deklariert, nicht in der Sequenz. |

### Beschreibungstexte

Zusätzlich zum `DisplayName` legt der Designer für sprachabhängige Beschreibungen drei Variablen
in der Sequenz an:

```xml
<Variable x:TypeArguments="x:String" Default="45a82258-2b01-4f7a-a9f2-9ccb5c941acd" Name="stepLabelLabelId" />
<Variable x:TypeArguments="x:String" Name="stepLabelDescription">
  <Variable.Default><Literal x:TypeArguments="x:String" Value="" /></Variable.Default>
</Variable>
<Variable x:TypeArguments="x:Int32" Default="1031" Name="stepLabelLanguageCode" />
```

> [!NOTE]
> `stepLabelLabelId` wird bei **jedem** Schreibvorgang neu erzeugt. Der Wert ist beliebig, muss
> aber vorhanden sein. `stepLabelLanguageCode` ist die LCID der Designer-Sprache (1031 = Deutsch).

## Aktivitätsreferenz

Alle `AssemblyQualifiedName`-Angaben der Plattformaktivitäten folgen dem Schema:

```
Microsoft.Crm.Workflow.Activities.<Name>, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35
```

| Schritt | Aktivität | Anmerkung |
|---|---|---|
| Überprüfungsbedingung | `ConditionSequence` (als `ActivityReference`) | Argument `Wait=False` |
| Wartebedingung | `ConditionSequence` | Argument `Wait=True`; `ContainsElseBranch` ist `x:Null` |
| Bedingungszweig | `ConditionBranch` | Argument `Condition` = Variablenverweis; beim Else-Zweig das Literal `True` |
| Zweig-/Phasen-Inhalt | `Composite` | Wrapper um eine `Sequence` |
| Datensatz erstellen | `mxswa:CreateEntity` | Ergebnis in `CreatedEntities("<StepId>_localParameter")` |
| Datensatz aktualisieren | `mxswa:UpdateEntity` | Temporärmuster |
| Datensatz zuweisen | `mxswa:AssignEntity` | Attribut `Owner` |
| Feldwert lesen | `mxswa:GetEntityProperty` | Attribute `Attribute`, `Entity`, `EntityName`, `Value` |
| Feldwert setzen | `mxswa:SetEntityProperty` | dito, plus `TargetType` |
| E-Mail senden | `mxswa:SendEmail` | Temporärmuster mit `New Entity("email")` |
| Untergeordneten Workflow starten | `mxswa:StartChildWorkflow` | `WorkflowId`, `InputParameters` (Dictionary-Variable) |
| Status ändern | `mxswa:SetState` | `State`/`Status` als `OptionSetValue`-Literale; **ohne** Sequenz-Wrapper |
| Aktion durchführen | `mcwa:InvokeSdkMessageActivity` | `SdkMessageId`, `SdkMessageName`, `SdkMessageEntityName` |
| Workflow beenden | `TerminateWorkflow` (WF4) | `Exception`, `Reason` |
| Ausdruck auswerten | `EvaluateExpression` | siehe [Wertausdrücke](#wertausdrücke) |
| Bedingung auswerten | `EvaluateCondition` | `ConditionOperator`, `Operand`, `Parameters`, `Result` |
| Logische Verknüpfung | `EvaluateLogicalCondition` | `LogicalOperator` (`And`/`Or`), `LeftOperand`, `RightOperand` |
| Typkonvertierung | `ConvertCrmXrmTypes` | CRM-Typ → .NET-Typ |
| Codeaktivität | `AssemblyQualifiedName` der eigenen Assembly | siehe unten |

### Beispiel: Überprüfungsbedingung

Eine Bedingung besteht aus einem `ConditionSequence`-Container mit vier Aktivitäten:

```xml
<mxswa:ActivityReference AssemblyQualifiedName="…ConditionSequence, …" DisplayName="ConditionStep1">
  <mxswa:ActivityReference.Arguments>
    <InArgument x:TypeArguments="x:Boolean" x:Key="Wait">False</InArgument>
  </mxswa:ActivityReference.Arguments>
  <mxswa:ActivityReference.Properties>
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables">
      <Variable x:TypeArguments="x:Boolean" Default="False" Name="ConditionBranchStep2_condition" />
      <Variable x:TypeArguments="x:Object" Name="ConditionBranchStep2_1" />
      <Variable x:TypeArguments="x:Object" Name="ConditionBranchStep2_2" />
    </sco:Collection>
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
      <!-- 1. linke Seite lesen -->
      <mxswa:GetEntityProperty Attribute="lastname" Entity='[InputEntities("primaryEntity")]'
                               EntityName="lead" Value="[ConditionBranchStep2_1]">
        <mxswa:GetEntityProperty.TargetType>
          <InArgument x:TypeArguments="s:Type"><mxswa:ReferenceLiteral x:TypeArguments="s:Type"><x:Null /></mxswa:ReferenceLiteral></InArgument>
        </mxswa:GetEntityProperty.TargetType>
      </mxswa:GetEntityProperty>
      <!-- 2. rechte Seite aufbereiten (Literal oder zweites GetEntityProperty) -->
      <!-- 3. vergleichen -->
      <mxswa:ActivityReference AssemblyQualifiedName="…EvaluateCondition, …" DisplayName="EvaluateCondition">
        <mxswa:ActivityReference.Arguments>
          <InArgument x:TypeArguments="mxsq:ConditionOperator" x:Key="ConditionOperator">Equal</InArgument>
          <InArgument x:TypeArguments="s:Object[]" x:Key="Parameters">[New Object() { ConditionBranchStep2_2 }]</InArgument>
          <InArgument x:TypeArguments="x:Object" x:Key="Operand">[ConditionBranchStep2_1]</InArgument>
          <OutArgument x:TypeArguments="x:Boolean" x:Key="Result">[ConditionBranchStep2_condition]</OutArgument>
        </mxswa:ActivityReference.Arguments>
      </mxswa:ActivityReference>
      <!-- 4. verzweigen -->
      <mxswa:ActivityReference AssemblyQualifiedName="…ConditionBranch, …" DisplayName="ConditionBranchStep2">
        <mxswa:ActivityReference.Arguments>
          <InArgument x:TypeArguments="x:Boolean" x:Key="Condition">[ConditionBranchStep2_condition]</InArgument>
        </mxswa:ActivityReference.Arguments>
        <mxswa:ActivityReference.Properties>
          <x:Null x:Key="Then" /><x:Null x:Key="Else" /><x:Null x:Key="Description" />
        </mxswa:ActivityReference.Properties>
      </mxswa:ActivityReference>
    </sco:Collection>
    <x:Boolean x:Key="ContainsElseBranch">False</x:Boolean>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

Wird ein Zweig mit Schritten gefüllt, ersetzt ein `Composite`-Wrapper das `x:Null` der
Eigenschaft `Then` bzw. `Else`. Der Standardaktionszweig ist ein weiterer `ConditionBranch` mit
`Condition` = `True`; zusätzlich wechselt `ContainsElseBranch` auf `True`.

> [!IMPORTANT]
> **Wertlose Operatoren** (`Null`, `NotNull` — in der Oberfläche „enthält keine Daten" bzw. „enthält
> Daten") haben keinen Vergleichswert. `Parameters` muss dann als **explizites Null-Element**
> geschrieben werden, nicht als `InArgument`:
>
> ```xml
> <InArgument x:TypeArguments="mxsq:ConditionOperator" x:Key="ConditionOperator">NotNull</InArgument>
> <x:Null x:Key="Parameters" />
> <InArgument x:TypeArguments="x:Object" x:Key="Operand">[ConditionBranchStep2_2]</InArgument>
> ```
>
> Ein leeres Array (`[New Object() { }]`) wird von der Plattform mit `0x80045040` abgelehnt. Dies ist
> die in der Praxis häufigste Ursache für abgelehntes XAML.

### Phase

Eine Phase ist ein `Composite`-Wrapper ohne besondere Kennzeichnung:

```xml
<mxswa:ActivityReference AssemblyQualifiedName="…Composite, …"
                         DisplayName="StageStep16: Beschreibung der Phase">
  <mxswa:ActivityReference.Properties>
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
      … Schritte …
      <Persist />
    </sco:Collection>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

> [!NOTE]
> Eine Phase unterscheidet sich strukturell **nicht** von einem Zweig-Wrapper. Sie wird
> ausschließlich über die Schritt-ID-Konvention `StageStep<N>` im `DisplayName` erkannt.
> Enthält ein Prozess Phasen, müssen alle Schritte in Phasen liegen; der Designer ergänzt beim
> Einfügen gegebenenfalls automatisch eine führende Phase.

## Wertausdrücke

Literale werden **nie** direkt in ein Zielattribut geschrieben. Jeder Wert wird über eine
Hilfsaktivität in eine Variable aufbereitet und von dort referenziert.

### Statischer Wert

`EvaluateExpression` mit `ExpressionOperator` = `CreateCrmType`:

```xml
<InArgument x:Key="ExpressionOperator">CreateCrmType</InArgument>
<InArgument x:Key="Parameters">[New Object() { Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.String, "Text", "String" }]</InArgument>
<InArgument x:Key="TargetType"><mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="x:String" /></InArgument>
<OutArgument x:Key="Result">[UpdateStep3_4]</OutArgument>
```

Der erste Parameter ist der `WorkflowPropertyType`, der zweite der Wert, der dritte dessen
Typname.

### Feldverweise mit Standardwert

`EvaluateExpression` mit `ExpressionOperator` = `SelectFirstNonNull`. Die Quellen werden zuvor je
mit `GetEntityProperty` gelesen; der Standardwert wird als Literal aufbereitet und als
**letztes** Element des Parameterarrays angefügt:

```xml
<!-- GetEntityProperty companyname → [UpdateStep3_2] -->
<!-- GetEntityProperty subject     → [UpdateStep3_3] -->
<!-- CreateCrmType "unbekannt"     → [UpdateStep3_4] -->
<InArgument x:Key="ExpressionOperator">SelectFirstNonNull</InArgument>
<InArgument x:Key="Parameters">[New Object() { UpdateStep3_2, UpdateStep3_3, UpdateStep3_4 }]</InArgument>
<OutArgument x:Key="Result">[UpdateStep3_1]</OutArgument>
```

Zur Laufzeit gewinnt der erste nicht leere Wert.

### Ausgabe eines vorangehenden Schritts

```xml
<InArgument x:Key="ExpressionOperator">SelectFirstNonNull</InArgument>
<InArgument x:Key="Parameters">[New Object() { CustomActivityStep3Domain_localParameter }]</InArgument>
<OutArgument x:Key="Result">[UpdateStep6_1]</OutArgument>
```

### Vergleichswert einer Bedingung

Hier gilt eine Ausnahme: Ein Feldverweis auf der rechten Seite eines Vergleichs ersetzt den
Literalblock **ersatzlos durch ein zweites `GetEntityProperty`**, das in dieselbe Variable
schreibt. Weder `SelectFirstNonNull` noch `ConvertCrmXrmTypes` kommen zum Einsatz.

### Typkonvertierung

`ConvertCrmXrmTypes` ist nur erforderlich, wenn der Wert in ein Argument einer **Codeaktivität**
fließt, weil dort ein echter .NET-Typ erwartet wird:

```xml
<InArgument x:Key="Value">[CustomActivityStep3_1]</InArgument>
<InArgument x:Key="TargetType"><mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="x:String" /></InArgument>
<OutArgument x:Key="Result">[CustomActivityStep3_1_converted]</OutArgument>
```

Die Verwendung erfolgt dann typisiert: `[DirectCast(CustomActivityStep3_1_converted, System.String)]`.

`SetEntityProperty` benötigt keine Konvertierung, da sein `Value` einen CRM-typisierten Wert annimmt.

### `TargetType` von `GetEntityProperty`

| Kontext | Wert |
|---|---|
| Bedingungsvergleich | `<mxswa:ReferenceLiteral><x:Null /></mxswa:ReferenceLiteral>` |
| Wertaufbereitung für Felder/Argumente | `<mxswa:ReferenceLiteral Value="x:String" />` (bzw. Zieltyp) |

## Benutzerdefinierte Workflowaktivitäten

### Parametermetadaten

Die Ein- und Ausgabeparameter registrierter Codeaktivitäten liegen als XML in
`plugintype.customworkflowactivityinfo`:

```
GET /api/data/v9.2/plugintypes(<pluginTypeId>)?$select=name,customworkflowactivityinfo,workflowactivitygroupname
```

```xml
<SandboxCustomActivityInfo>
  <CustomActivityInfo>
    <Name>Contoso.Plugins.Workflows.ExtractDomain</Name>
    <GroupName>Contoso.Plugins (1.0.0.0)</GroupName>
    <PublicKeyToken>6cd3b47345c1c112</PublicKeyToken>
    <Culture>neutral</Culture>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
  </CustomActivityInfo>
  <Inputs>
    <CustomActivityParameterInfo>
      <Name>E-Mail</Name>
      <TypeName>System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</TypeName>
      <Required>false</Required>
      <WorkflowAttributeType>Boolean</WorkflowAttributeType>
      <DependencyPropertyName>Email</DependencyPropertyName>
    </CustomActivityParameterInfo>
  </Inputs>
  <Outputs>…</Outputs>
  <AssemblyQualifiedName>Contoso.Plugins.Workflows.ExtractDomain, Contoso.Plugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=6cd3b47345c1c112</AssemblyQualifiedName>
</SandboxCustomActivityInfo>
```

> [!IMPORTANT]
> - Der `AssemblyQualifiedName` liegt **fertig zusammengesetzt** vor und sollte übernommen, nicht
>   selbst gebildet werden. Insbesondere ist `PublicKeyToken` bei signierten Assemblys gesetzt.
> - Als `x:Key` im XAML dient `DependencyPropertyName`, **nicht** `Name`. Im Beispiel oben:
>   `Email`, nicht `E-Mail`.
> - `WorkflowAttributeType` ist unzuverlässig — im Beispiel steht `Boolean` bei einem
>   Zeichenfolgenparameter. Maßgeblich ist `TypeName`.
> - `workflowactivitygroupname` ist eine **Zeichenfolge** (`"Contoso.Plugins (1.0.0.0)"`).

### XAML-Darstellung

Die Aktivität steckt in einem `Composite`-Wrapper; Parameter erscheinen als `InArgument` bzw.
`OutArgument` mit `x:Key` = `DependencyPropertyName`:

```xml
<mxswa:ActivityReference AssemblyQualifiedName="…Composite, …" DisplayName="CustomActivityStep3">
  <mxswa:ActivityReference.Properties>
    <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />
    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
      <mxswa:ActivityReference
          AssemblyQualifiedName="Contoso.Plugins.Workflows.ExtractDomain, Contoso.Plugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=6cd3b47345c1c112"
          DisplayName="CustomActivityStep3: Extract Domain (CodeActivity)">
        <mxswa:ActivityReference.Arguments>
          <InArgument  x:TypeArguments="x:String" x:Key="Website">[DirectCast(CustomActivityStep3_1_converted, System.String)]</InArgument>
          <InArgument  x:TypeArguments="x:String" x:Key="Email">[DirectCast(CustomActivityStep3_3_converted, System.String)]</InArgument>
          <OutArgument x:TypeArguments="x:String" x:Key="Domain">[CustomActivityStep3Domain_localParameter]</OutArgument>
        </mxswa:ActivityReference.Arguments>
      </mxswa:ActivityReference>
    </sco:Collection>
  </mxswa:ActivityReference.Properties>
</mxswa:ActivityReference>
```

Die Ausgabevariable wird auf Workflowebene deklariert:

```xml
<mxswa:Workflow.Variables>
  <Variable x:TypeArguments="x:String" Default="[Nothing]" Name="CustomActivityStep3Domain_localParameter" />
</mxswa:Workflow.Variables>
```

## Aktivierung und Kompilierung

Die Aktivierung erfolgt über eine Zustandsänderung des Datensatzes:

```http
PATCH /api/data/v9.2/workflows(<id>)
{ "statecode": 1, "statuscode": 2 }
```

Dabei kompiliert die Plattform das XAML und **ersetzt die Null-GUID im Klassennamen durch die
tatsächliche `workflowid`** — an allen Vorkommen (`x:Class` sowie den `this:`-Eigenschaftselementen):

```
vor  Aktivierung:  x:Class="XrmWorkflow00000000000000000000000000000000"
nach Aktivierung:  x:Class="XrmWorkflowe784a882c8ef46b2b6e400cbe9146360"
```

> [!NOTE]
> Der Klassenname muss beim Schreiben **nicht** die Prozess-GUID enthalten; die Null-GUID ist
> zulässig. Ebenso sind unterschiedliche Assemblyversionen in den Namespaces tolerant: In einer
> Organisation koexistieren Prozesse mit `Version=8.0.0.0` und `Version=9.0.0.0`.
>
> Die Aktivierung validiert die Ablauflogik nicht vollständig. Unvollständig konfigurierte
> Schritte (etwa ein Erstellungsschritt ohne Pflichtfelder) verhindern die Aktivierung nicht.

## Programmatischer Zugriff

### Unterstützte und nicht unterstützte Vorgänge

| Vorgang | Weg | Ergebnis |
|---|---|---|
| Prozesse lesen | `GET /api/data/v9.2/workflows` | unterstützt |
| XAML lesen | `GET …/workflows(<id>)?$select=xaml` | unterstützt |
| Metadaten ändern | `PATCH …/workflows(<id>)` | unterstützt |
| **XAML ändern** | `PATCH …/workflows(<id>)` mit `{"xaml": …}` | **unterstützt, solange `statecode=0`** |
| Aktivieren/Deaktivieren | `PATCH` auf `statecode`/`statuscode` | unterstützt, Trigger erforderlich |
| Prozess erstellen **ohne** `xaml` | `POST /api/data/v9.2/workflows` | scheitert mit `0x80045040` |
| **Prozess erstellen mit `xaml`** | `POST /api/data/v9.2/workflows` | **unterstützt** |
| Prozess löschen | `DELETE …/workflows(<id>)` | unterstützt, nur im Entwurf |

> [!IMPORTANT]
> `0x80045040` ("außerhalb der Webanwendung erstellt") bezieht sich auf **fehlendes oder ungültiges
> XAML**, nicht auf den Zugriffsweg. Ein `POST` mit gültigem Grundgerüst im Feld `xaml` wird
> akzeptiert — verifiziert mit Bearer-Token gegen Dataverse 9.2. Der interne SOAP-Dienst ist dafür
> nicht nötig (und für API-Aufrufer ohnehin gesperrt).
>
> Dasselbe gilt beim `PATCH`: Wird XAML geschrieben, das die Plattform nicht als gültig ansieht,
> lautet die Antwort ebenfalls `0x80045040` — obwohl der Datensatz längst existiert. Die Meldung ist
> also ein allgemeiner „XAML nicht akzeptiert"-Fehler.

### Bearbeitungszyklus für XAML

Der folgende Zyklus ist verifiziert: Lesen → Ändern → Zurückschreiben → Aktivieren → Öffnen im
Designer, wobei der Designer die Änderung korrekt darstellt und anschließend selbst darauf
weiterarbeitet.

```
1. GET    …/workflows(<id>)?$select=xaml,statecode
2. ggf. PATCH { "statecode": 0, "statuscode": 1 }        // Entwurf erzwingen
3. XAML ändern – Namenskonventionen einhalten!
4. PATCH  …/workflows(<id>)  { "xaml": "…" }             // 204 No Content
5. PATCH  …/workflows(<id>)  { "statecode": 1, "statuscode": 2 }
```

> [!TIP]
> Vor Schritt 4 immer das unveränderte XAML als Wiederherstellungspunkt sichern.

## Fehlerreferenz

| Code | Meldung (gekürzt) | Ursache und Abhilfe |
|---|---|---|
| `0x80045040` | „Dieser Workflow kann nicht erstellt, aktualisiert oder veröffentlicht werden, da er außerhalb der Microsoft Dynamics 365-Webanwendung erstellt wurde." | Das XAML fehlt oder wird nicht akzeptiert. Beim Erstellen ein gültiges Grundgerüst mitsenden. Beim Ändern: die Namenskonventionen und die unten genannten Detailregeln prüfen (häufigste Ursache: siehe `x:Null`-Regel bei wertlosen Operatoren). |
| `0x80045018` | „Automatic workflow cannot be published if no activation parameters have been specified." | Aktivierung eines automatischen Prozesses ohne Auslöser. Vorher `triggeroncreate`/`updatestage`/`triggerondelete` oder `ondemand` setzen. |
| `INVALID_WRPC_TOKEN` | SOAP-Fault von `Workflow.asmx` | Der interne Dienst verlangt das Anti-Forgery-Token des Legacy-Webclients. Web API verwenden. |
| `0x80045037` | Designer kann den Prozess nicht darstellen | Strukturell inkonsistentes XAML: Schritt-IDs, `DisplayName` oder Variablennamen entsprechen nicht den Konventionen. Der Fehler entsteht **nicht** durch das Schreiben an sich. |
| `0x80060888` | „Resource not found for the segment 'plugintypeattributes'." | Die Tabelle `plugintypeattributes` existiert in aktuellen Dataverse-Versionen nicht. Parameter von Codeaktivitäten stattdessen aus `plugintype.customworkflowactivityinfo` lesen. |

## Einschränkungen und Hinweise

- **Alle Schnittstellen dieses Dokuments außer der Web-API sind intern.** `Workflow.asmx` ist
  nicht versioniert und kann sich ändern.
- Die Zuordnung von Schritt-IDs ist prozessweit eindeutig und fortlaufend. Beim Einfügen von
  Schritten in bestehende Prozesse muss der Zähler fortgesetzt werden.
- Der Designer erzeugt auch **unvollständige Zwischenstände** im XAML (etwa `CreateEntity` mit
  leerem `EntityName`). Ein Entwurf muss nicht ausführbar sein.
- Änderungen an aktivierten Prozessen sind nicht möglich; vorher deaktivieren.
- Beim Wechsel der Zielentität eines Schritts verwirft der Designer die Konfiguration dieses
  Schritts (Rückfrage im Browser).

### Hinweise zur Automatisierung der Oberfläche

| Element | ID/Muster |
|---|---|
| Menü „Schritt hinzufügen" | `mnu_AddStep_<Typ>`, Codeaktivitäten `CustomActivity<pluginTypeId>` |
| Schaltfläche „Eigenschaften festlegen" | `<StepId>_button` |
| Entitätsauswahl eines Schritts | `<StepId>_entitylstwfc` (löst eine Rückfrage aus) |
| Bedingungszeile (Advanced-Find-Steuerung) | `EFGRP<id>` + `EFENTITYCTL` / `AFATTRCTL` / `OPFOPCTL` / `VFVALUECTL` |
| Formular-Assistent | `selObjects` (Entität), `valueSelector` (Feld), `wfDynamicExpressionAdd`, `dynamicValueSelector`, `<zielfeld>DefaultValueControl`, `wfDynamicExpressionOk` |
| Übertragung eines Feldverweises | JS-Funktion `InsertCustomizedDataSlug(value, text)` |

Die vier Steuerelemente einer Bedingungszeile werden kaskadierend geladen (Entität → Attribut →
Operator → Wert); jede Stufe wird erst nach Auswahl der vorherigen gefüllt. Die Feldliste ist
typgefiltert: Im Bedingungseditor erscheinen nur Felder, die zum Datentyp des linken Operanden
passen.

## Anhang: Untersuchungsmethodik

Die Angaben dieses Dokuments beruhen auf folgendem Vorgehen:

1. **Netzwerk-Mitschnitt** aller Anfragen des Designers je Bearbeitungsschritt, inklusive
   vollständiger SOAP-Rümpfe.
2. **Differenzanalyse des XAML**: Nach jeder einzelnen Designer-Aktion wurde das XAML über die
   Web-API gelesen und gegen den Vorzustand verglichen. Das isoliert das Delta eines Schritttyps
   exakt.
3. **Gegenprobe an produktiven Prozessen**: Vollständig konfigurierte Muster (Codeaktivitäten mit
   Parametern, Feldverweise) wurden an bestehenden, von Menschen erstellten Prozessen verifiziert.
4. **Schreibprobe**: Geändertes XAML wurde zurückgeschrieben, der Prozess aktiviert und im
   Designer erneut geöffnet, um Rückwärtskompatibilität zu belegen.

Empfohlener Ablauf für weitere Untersuchungen:

```bash
# XAML lesen, lesbar umbrechen, gegen Vorzustand diffen
sed 's/></>\n</g' schritt-N.xaml > schritt-N.pretty
diff schritt-N-1.pretty schritt-N.pretty
```

## Nachtrag: Die Aktivierung validiert das XAML gründlich

Frühere Annahme in diesem Dokument war, die Aktivierung prüfe die Logik kaum. Das gilt nur für die
*Vollständigkeit der Konfiguration* (ein Schritt ohne Pflichtangaben lässt sich aktivieren). Die
*Struktur* wird dagegen genau geprüft, und der Fehler nennt die betroffenen Schritte:

```
0x80048455 — Dieser Workflow enthaelt Fehler und kann nicht veroeffentlicht werden.
Worklfow Id: <id der Aktivierungskopie>
ErrorMap Details: {CustomActivityStep9: InvalidPropertyBag ;
                   ConditionBranchStep8: InvalidEntity, InvalidPropertyBag ;
                   ... ; WorkflowStep0: InvalidEntity, InvalidPropertyBag}
```

| Kennzeichen | Bedeutung |
|---|---|
| `InvalidEntity` | Der Schritt liest von einer Entität, die in diesem Kontext nicht auflösbar ist — etwa ein `related_…#…`-Schlüssel, dessen Lookup-Attribut nicht zur genannten Zielentität führt. |
| `InvalidPropertyBag` | Die `ActivityReference.Properties` eines Schritts passen nicht zur erwarteten Form der Aktivität. |

> [!TIP]
> Die `ErrorMap` ist das wichtigste Diagnosewerkzeug. Sie erscheint **nur** beim Aktivieren, nicht
> beim Schreiben des XAML — ein `PATCH` kann also erfolgreich sein, obwohl der Prozess nicht
> lauffähig ist. Wer XAML generiert, sollte nach dem Schreiben immer eine Probeaktivierung machen.
>
> Beachte auch: Die genannte Workflow-Id ist die der **Aktivierungskopie** (`type=2`), nicht die des
> bearbeiteten Prozesses.
