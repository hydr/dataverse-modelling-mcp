namespace Dataverse.Tests.BusinessProcessFlows;

using Dataverse.Core.BusinessProcessFlows;
using Dataverse.Core.Workflows;

/// <summary>Synthetic definitions and ids shared by the business-process-flow tests.</summary>
internal static class BpfTestData
{
    public static readonly Guid ProcessId = Guid.Parse("11111111-1111-1111-1111-111111110001");
    public static readonly Guid RecalculateWorkflow = Guid.Parse("11111111-1111-1111-1111-111111110101");
    public static readonly Guid NotifyAction = Guid.Parse("11111111-1111-1111-1111-111111110102");
    public static readonly Guid ResearchFlow = Guid.Parse("11111111-1111-1111-1111-111111110103");
    public static readonly Guid AuditWorkflow = Guid.Parse("11111111-1111-1111-1111-111111110104");

    /// <summary>Knows the processes the definitions refer to — the builder needs their names.</summary>
    public static BpfCatalog Catalog()
    {
        var catalog = new BpfCatalog();
        catalog.AddProcess(RecalculateWorkflow,
            new BpfProcessInfo(RecalculateWorkflow, "Recalculate score", null, 0, "account", true, true));
        catalog.AddProcess(NotifyAction,
            new BpfProcessInfo(NotifyAction, "Notify owner", "sample_NotifyOwner", 3, "account", true, true));
        catalog.AddProcess(ResearchFlow,
            new BpfProcessInfo(ResearchFlow, "Company research", null, 5, "none", true, false));
        catalog.AddProcess(AuditWorkflow,
            new BpfProcessInfo(AuditWorkflow, "Write audit entry", null, 0, "account", true, true));
        return catalog;
    }

    /// <summary>Account → branch → contact, with every kind of step and trigger.</summary>
    public static BpfDefinition Full() => new()
    {
        PrimaryEntity = "account",
        LanguageCode = 1033,
        Workflows = [new BpfWorkflowTrigger { WorkflowId = AuditWorkflow.ToString(), On = BpfTriggerEvent.Finished }],
        Stages =
        [
            new BpfStage
            {
                Name = "Capture",
                Category = "Qualify",
                Steps =
                [
                    new BpfStep { Attribute = "name", Required = true },
                    new BpfStep { Kind = BpfStepKind.Action, ProcessId = RecalculateWorkflow.ToString(), Label = "Recalculate" },
                    new BpfStep { Kind = BpfStepKind.Action, ProcessId = NotifyAction.ToString() }
                ],
                Workflows = [new BpfWorkflowTrigger { WorkflowId = AuditWorkflow.ToString(), On = BpfTriggerEvent.StageExit }]
            },
            new BpfStage
            {
                Name = "Assess",
                Steps = [new BpfStep { Attribute = "numberofemployees", Required = true }, new BpfStep { Attribute = "sample_score", Required = true }],
                Branch = new BpfBranching
                {
                    Branches =
                    [
                        new BpfBranch
                        {
                            Description = "Large",
                            LogicalOperator = "Or",
                            Conditions =
                            [
                                new WorkflowCondition
                                {
                                    Attribute = "numberofemployees", Operator = "GreaterEqual",
                                    Value = new WorkflowValue { Kind = WorkflowValueKind.Literal, DataType = "Integer", Literal = "100" }
                                },
                                new WorkflowCondition { Attribute = "sample_score", Operator = "NotNull" }
                            ],
                            Next = "Large"
                        }
                    ],
                    Else = "Small"
                }
            },
            new BpfStage { Name = "Large", Steps = [new BpfStep { Attribute = "description" }], Next = "Contact" },
            new BpfStage { Name = "Small", Steps = [new BpfStep { Attribute = "websiteurl" }] },
            new BpfStage
            {
                Name = "Contact",
                Entity = "contact",
                Relationship = new BpfRelationship { Name = "contact_customer_accounts", Attribute = "parentcustomerid" },
                Steps =
                [
                    new BpfStep { Attribute = "jobtitle" },
                    new BpfStep { Kind = BpfStepKind.Flow, ProcessId = ResearchFlow.ToString(), Required = true }
                ]
            }
        ]
    };
}
