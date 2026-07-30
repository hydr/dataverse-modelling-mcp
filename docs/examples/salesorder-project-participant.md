# Beispiel: Salesbeteiligten auf dem Auftrag ermitteln

Fachliche Anforderung (Testfall, Umgebung `contoso-dev`):

1. Bei **Anlage eines Auftrags** prüfen, ob `sample_projektbeteiligter1` bereits gefüllt ist.
2. Wenn nicht: prüfen, ob eine Verkaufschance verknüpft ist und dort `sample_salesma` gefüllt ist.
3. Wenn ja: diesen Wert übernehmen.
4. Sonst: prüfen, ob `customerid` (Kunde/Firma) gefüllt ist.
5. Wenn ja: den Besitzer dieser Firma (`account.ownerid`) ermitteln.
6. Wenn dieser Besitzer Mitglied des Teams **Salesteam** ist: ihn in `sample_projektbeteiligter1` schreiben.

## Beteiligte Felder

| Zweck | Feld | Typ |
|---|---|---|
| Ziel | `salesorder.sample_projektbeteiligter1` | Lookup |
| Verkaufschance | `salesorder.opportunityid` | Lookup → opportunity |
| Projektbeteiligter dort | `opportunity.sample_salesma` | Lookup |
| Kunde/Firma | `salesorder.customerid` | Customer (account **oder** contact!) |
| Firma (reines Konto) | `salesorder.accountid` | Lookup → account |
| Besitzer der Firma | `account.ownerid` | Owner |
| Team | „Salesteam" | `a0000001-0000-4000-8000-000000000001` |

> [!NOTE]
> `customerid` ist ein **Customer**-Feld und kann auf `account` *oder* `contact` zeigen. Für Schritt 5
> ist `accountid` der verlässlichere Einstieg, weil der `related_…`-Zugriff eine feste Zielentität
> braucht. Alternativ über `customeridtype` verzweigen.

## Zielzustand als Definition

Umgesetzt und verifiziert: `SalesOrderParticipantWorkflowTests` baut genau diese Definition in
`contoso-dev`, aktiviert sie und liest sie wieder aus.

```json
{
  "primaryEntity": "salesorder",
  "steps": [
    {
      "kind": "condition",
      "description": "Salesbeteiligter noch leer",
      "conditions": [
        { "attribute": "sample_projektbeteiligter1", "operator": "Null" }
      ],
      "then": [
        {
          "kind": "condition",
          "description": "Verkaufschance mit Projektbeteiligtem vorhanden",
          "conditions": [
            { "attribute": "opportunityid", "operator": "NotNull" },
            { "entity": "opportunity", "via": "opportunityid",
              "attribute": "sample_salesma", "operator": "NotNull" }
          ],
          "logicalOperator": "And",
          "then": [
            {
              "kind": "updateRecord",
              "description": "Salesbeteiligten aus Verkaufschance uebernehmen",
              "attributes": [
                { "attribute": "sample_projektbeteiligter1",
                  "value": { "kind": "field", "dataType": "EntityReference",
                             "fields": ["opportunity.sample_salesma"], "via": "opportunityid" } }
              ]
            }
          ],
          "else": [
            {
              "kind": "condition",
              "description": "Firma am Auftrag vorhanden",
              "conditions": [
                { "attribute": "accountid", "operator": "NotNull" }
              ],
              "then": [
                {
                  "kind": "customActivity",
                  "description": "Ist der Firmenbesitzer im Salesteam",
                  "assemblyQualifiedName": "msdyncrmWorkflowTools.CheckUserInTeam, msdyncrmWorkflowTools, Version=1.0.62.1, Culture=neutral, PublicKeyToken=416e876b9bee261e",
                  "inputs": {
                    "Team": { "kind": "literal", "dataType": "EntityReference",
                              "literal": "team:a0000001-0000-4000-8000-000000000001" },
                    "User": { "kind": "field", "dataType": "EntityReference",
                              "fields": ["account.ownerid"], "via": "accountid" }
                  },
                  "outputs": ["isUserInTeam"]
                },
                {
                  "kind": "condition",
                  "description": "Besitzer ist im Salesteam",
                  "conditions": [
                    { "stepOutput": "isUserInTeam",
                      "operator": "Equal",
                      "value": { "kind": "literal", "dataType": "Boolean", "literal": "true" } }
                  ],
                  "then": [
                    {
                      "kind": "updateRecord",
                      "description": "Firmenbesitzer als Salesbeteiligten setzen",
                      "attributes": [
                        { "attribute": "sample_projektbeteiligter1",
                          "value": { "kind": "field", "dataType": "EntityReference",
                                     "fields": ["account.ownerid"], "via": "accountid" } }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

Anschließend:

```
workflow_update(id, {"triggeroncreate": true, "createstage": 40})
workflow_set_state(id, activate=true)
```

## Die drei Builder-Fähigkeiten, die dafür nötig waren

| # | Fähigkeit | Umsetzung |
|---|---|---|
| 1 | Felder verknüpfter Datensätze lesen | `via` (Lookup-Attribut) an `WorkflowCondition` und `WorkflowValue`; der Builder erzeugt `Entity="[InputEntities("related_<via>#<entity>")]"` mit `EntityName="<entity>"`. `WF079`/`WF122` verlangen `via`, sobald die Entität nicht die Primärentität ist. |
| 2 | Bedingung auf einer Aktivitäts-Ausgabe | `WorkflowCondition.StepOutput` als Alternative zu `Attribute`; die `_localParameter`-Variable wird `Operand`, ohne `GetEntityProperty`. |
| 3 | `EntityReference`-Literale | Schreibweise `"<entität>:<guid>"`; zwei `CreateCrmType`-Schritte (Guid mit Marker `UniqueIdentifier`, dann die Referenz mit leerem Label und Marker `Lookup`). |

Dazu kam, was die Aktivierung eigentlich blockierte: Argumenttypen aus
`plugintype.customworkflowactivityinfo` statt hart `x:String`, und das Namensschema der konvertierten
Hilfsvariablen — Details in `custom-activity-xaml-reference.md`.

## Zurücklesen

`workflow_get_definition` rekonstruiert auch die **Eingaben** des `customActivity`-Schritts: das feste
Team kommt als `"team:<guid>"` zurück, der Firmenbesitzer als Feldverweis mit `via`. Der Workflow
bleibt damit über `workflow_set_definition` änderbar.

Belegt gegen echtes Designer-XAML, nicht nur gegen selbst erzeugtes:
`DesignerXamlReadingTests` liest die Fixture `designer-custom-activity.xaml` — einen im Designer von
Hand konfigurierten Schritt — und baut daraus dasselbe XAML wieder auf.

Der härtere Beleg ist `PaymentReminderRebuildTests`: es liest den 133 KB grossen Workflow
"Zahlungserinnerung-Email verschicken" aus `contoso-dev`, baut ihn aus der Lesung neu und **aktiviert** die
Rekonstruktion als eigenen Workflow. Damit ist der Weg lesen -> aendern -> schreiben fuer einen
gewachsenen Workflow durchgehend belegt.
