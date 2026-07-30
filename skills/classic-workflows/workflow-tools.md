# Verknüpfte Datensätze und die Workflow Tools

Zwei Themen, die klassische Workflows über ihre scheinbaren Grenzen hinausheben. Ergänzung zu
`SKILL.md`.

## 1. Felder verknüpfter Datensätze lesen

Klassische Workflows können Felder **direkt verknüpfter** Datensätze lesen — eine Ebene tief, über
Lookup-Felder des Primärdatensatzes. Der Zugriff läuft über einen zweiten Schlüssel im
`InputEntities`-Wörterbuch:

```
InputEntities("related_<lookupAttribut>#<zielEntität>")
```

Beispiel — auf einem `salesorder` das Feld `sample_salesma` der verknüpften `opportunity` lesen
(Lookup-Feld dorthin: `opportunityid`):

```xml
<!-- Erst das Lookup-Feld des Primärdatensatzes lesen (macht die Verknüpfung verfügbar) -->
<mxswa:GetEntityProperty Attribute="opportunityid" Entity='[InputEntities("primaryEntity")]'
                         EntityName="salesorder" Value="[UpdateStep3_2]"> … </mxswa:GetEntityProperty>

<!-- Dann das Feld auf der verknüpften Entität -->
<mxswa:GetEntityProperty Attribute="sample_salesma"
                         Entity='[InputEntities("related_opportunityid#opportunity")]'
                         EntityName="opportunity" Value="[UpdateStep3_3]"> … </mxswa:GetEntityProperty>
```

Merkregeln:

- Der Schlüssel setzt sich aus **Lookup-Attribut des Primärdatensatzes** und **logischem Namen der
  Zielentität** zusammen, getrennt durch `#`, mit dem Präfix `related_`.
- `EntityName` ist die **Zielentität**, nicht die Primärentität.
- Nur eine Ebene. Für tiefere Pfade braucht es einen untergeordneten Workflow auf der Zielentität
  oder `msdyncrmWorkflowTools.QueryValues` (siehe unten).
- Die Plattform füllt diese Schlüssel selbst; man muss die Verknüpfung nicht „laden".

Im Definitionsmodell dieses Servers werden solche Verweise als `"<entität>.<attribut>"` geschrieben,
also `"opportunity.sample_salesma"` — der Builder erzeugt daraus den `related_…`-Schlüssel, sofern er
das Lookup-Attribut kennt (Angabe über `via`).

## 2. msdyncrmWorkflowTools — was klassische Workflows damit doch können

Die Assembly **msdyncrmWorkflowTools** (Version 1.0.62.1, `PublicKeyToken=416e876b9bee261e`) ist in
dieser Umgebung installiert und stellt **87 Codeaktivitäten** bereit. Sie schließen genau die Lücken,
an denen klassische Workflows sonst scheitern.

> [!TIP]
> Vor dem Griff zum Cloud Flow hier nachsehen. Vieles, was „mit klassischen Workflows nicht geht",
> geht mit diesen Aktivitäten doch — synchron und innerhalb der Transaktion.
>
> Parameter und den korrekten `AssemblyQualifiedName` immer per
> `workflow_get_activity_parameters` holen, nie raten.

### Beziehungen und Mitgliedschaften (nicht mit Bordmitteln prüfbar)

| Aktivität | Zweck |
|---|---|
| `CheckUserInTeam` | Ist ein Benutzer Mitglied eines Teams? In: `Team` (Lookup team), `User` (Lookup systemuser) · Out: `isUserInTeam` (Boolean) |
| `Class.IsMemberOfTeam` | dasselbe, alternative Implementierung |
| `CheckUserInRole` | Hat ein Benutzer eine bestimmte Sicherheitsrolle? |
| `Class.IsMemberOfMarketingList` | Mitgliedschaft in einer Marketingliste |
| `CheckAssociateEntity` | Besteht eine N:N-Verknüpfung? |
| `AssociateEntity` / `DisassociateEntity` | N:N-Verknüpfung setzen/lösen |
| `AddUserToTeam` / `RemoveUserFromTeam` | Teammitgliedschaft ändern |
| `AddRoleToUser` / `RemoveRoleFromUser` / `AddRoleToTeam` / `RemoveRoleFromTeam` | Rollen zuweisen |

### Daten abfragen und aggregieren

| Aktivität | Zweck |
|---|---|
| `QueryValues` | **Der Allzweck-Baustein:** FetchXML ausführen und Werte zurückgeben (7 In / 2 Out). Damit sind beliebig tiefe Beziehungen, Filter und Sortierungen erreichbar. |
| `CountChildEntityRecords` | Untergeordnete Datensätze zählen |
| `ConcatenateFromQuery` | Feldwerte mehrerer Datensätze zu einem Text verketten |
| `RollupFunctions` | Aggregate (5 Ausgaben) |
| `CalculateRollupField` | Rollup-Feld sofort neu berechnen |
| `GetRecordID` | Id des Datensatzes als Text |

### Datensätze verändern

| Aktivität | Zweck |
|---|---|
| `UpdateChildRecords` | Untergeordnete Datensätze in einem Zug aktualisieren |
| `CloneRecord` / `CloneChildren` | Datensätze samt Kindern kopieren |
| `Class.DeleteRecord` | Löschen (können klassische Workflows nicht) |
| `SetState` | Status setzen, auch wo der Standardschritt nicht greift |
| `SetLookupFieldFromRecordUrl` | Lookup aus einer Datensatz-URL setzen |
| `Class.SetProcess` / `SetProcessStage` | Geschäftsprozessfluss und Phase steuern |

### Optionssets, Text, Zahlen, Datum

| Aktivität | Zweck |
|---|---|
| `GetMultiSelectOptionSet` / `SetMultiSelectOptionSet` / `MapMultiSelectOptionSet` | Mehrfachauswahl-Felder (mit Bordmitteln unerreichbar) |
| `GetOptionSetValue` / `InsertOptionValue` / `DeleteOptionValue` | Optionswerte lesen und die Metadaten pflegen |
| `StringFunctions` | 12 Eingaben / 11 Ausgaben — Teilzeichenfolgen, Ersetzen, Suchen, Länge … |
| `NumericFunctions` / `DateFunctions` | Rechnen (4 bzw. 11 Ausgaben, u. a. Wochentag, Differenzen) |
| `CalculateAgregateDate` | Datumsaggregate |
| `CurrencyConvert` | Währungsumrechnung |
| `JsonParser` / `EntityJsonSerializer` | JSON lesen und schreiben |
| `EncryptText` / `TranslateText` | Verschlüsseln, übersetzen |

### Kommunikation, Freigaben, Sonstiges

| Aktivität | Zweck |
|---|---|
| `Class.EmailToTeam`, `Class.SendEmailToUsersInRole`, `Class.SendEmailFromTemplateToUsersInRole` | E-Mails an Teams und Rollen |
| `Class.EntityAttachmentToEmail`, `Class.SalesLiteratureToEmail` | Anlagen anhängen |
| `ShareRecordWithUser` / `ShareRecordWithTeam` (+ Unshare) | Freigaben setzen |
| `ShareSecuredField` | Feldsicherheit freigeben |
| `Class.ExecuteWorkflowByID`, `ExecuteWorkflowForRecordsinQuery` | Workflows dynamisch bzw. für Suchergebnisse starten |
| `Class.GetInitiatingUser` | Auslösenden Benutzer ermitteln |
| `RetrieveUserBUDefaultTeam` | Standardteam der Geschäftseinheit |
| `GetAppRecordUrl`, `GetAppModuleID`, `EntityMobileDeepLink` | Links in die App |
| `GeoCodeAddress` | Adresse geokodieren |
| `OrgDBSettingsRetrieve` / `OrgDBSettingsUpdate` | Organisationseinstellungen |
| `Class.PickFromQueue`, `QueueItemCount`, `ApplyRoutingRule`, `Class.ResolveCase`, `QualifyLead`, `WinQuote`, `CreateQuoteFromOpportunity`, `CalculatePrice` | Vorgangs- und Vertriebsautomatisierung |

### Vollständige Liste

87 Aktivitäten, ermittelbar mit:

```
GET /api/data/v9.2/plugintypes?$select=name,customworkflowactivityinfo
    &$filter=workflowactivitygroupname ne null and contains(assemblyname,'msdyncrmWorkflowTools')
```

oder über `workflow_list_activities` mit `nameFilter: "msdyncrmWorkflowTools"`.

## 3. Ausgaben einer Codeaktivität in Bedingungen prüfen

Eine Aktivität wie `CheckUserInTeam` liefert ihr Ergebnis in eine Variable
`<StepId><Parameter>_localParameter`. Um darauf zu verzweigen, wird diese Variable im
`EvaluateCondition` als `Operand` verwendet — statt eines `GetEntityProperty`-Ergebnisses:

```xml
<InArgument x:TypeArguments="mxsq:ConditionOperator" x:Key="ConditionOperator">Equal</InArgument>
<InArgument x:TypeArguments="s:Object[]" x:Key="Parameters">[New Object() { ConditionStepN_2 }]</InArgument>
<InArgument x:TypeArguments="x:Object" x:Key="Operand">[CustomActivityStepMisUserInTeam_localParameter]</InArgument>
```

Im Definitionsmodell: `conditions[].stepOutput` anstelle von `conditions[].attribute`. Dort genügt
der **reine Parametername** (`"isUserInTeam"`) — die Schritt-Id vergibt erst der Builder, kann also
beim Schreiben nicht bekannt sein. Die qualifizierte Form `"CustomActivityStep4.isUserInTeam"` wird
ebenfalls akzeptiert; beim Zurücklesen liefert der Server sie.

Der Typ der Variablen ist der **Parametertyp**, nicht `x:String` — bei `CheckUserInTeam` also
`x:Boolean` mit `Default="False"`. Der Builder liest ihn aus den Metadaten; ein hart gesetztes
`x:String` lehnt die Aktivierung als `InvalidPropertyBag` ab.

## 4. CRM-Typen der Workflow-Tools-Parameter

Die Aktivitäten verwenden CRM-eigene Parametertypen, nicht die .NET-Typen:

| `TypeName` im Metadaten-XML | Bedeutung | `dataType` im Modell |
|---|---|---|
| `Microsoft.Crm.Sdk.Lookup` | Datensatzverweis, gültige Zielentitäten stehen in `EntityNames` | `EntityReference` |
| `Microsoft.Crm.Sdk.CrmBoolean` | Ja/Nein | `Boolean` |
| `Microsoft.Crm.Sdk.CrmDateTime` | Datum/Zeit | `DateTime` |
| `Microsoft.Crm.Sdk.CrmDecimal` / `CrmFloat` / `CrmMoney` | Zahlen | `Decimal` / `Double` / `Money` |
| `Microsoft.Crm.Sdk.Picklist` | Optionsset | `OptionSetValue` |
| `System.String` | Text | `String` |

`EntityNames` eines Lookup-Parameters nennt die erlaubten Zielentitäten (z. B. `team`,
`systemuser`) — ein Lookup auf eine andere Entität wird zur Laufzeit abgelehnt. Bei festen
Referenzen (`"team:<guid>"`) prüft die Validierung das vorab (`WF088`); stammt der Wert aus einem
Feld, kann sie es nicht wissen.

Diese Zuordnung muss man nicht selbst treffen: `workflow_get_activity_parameters` liefert zu jedem
Parameter den passenden `dataType`, und ein falscher wird vor dem Schreiben als `WF086` gemeldet.
