namespace Dataverse.Tests.BusinessProcessFlows;

using System.Net;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Auth;
using Dataverse.Core.BusinessProcessFlows;
using Dataverse.Core.Clients;
using Dataverse.Core.Services;
using Dataverse.Core.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

/// <summary>
/// The service against a scripted Dataverse: each test registers the answers for the URLs it expects,
/// and every request is recorded, so a test can also assert what was <em>not</em> written.
/// </summary>
[TestFixture]
public sealed class BusinessProcessFlowServiceTests
{
    private const string OrgUrl = "https://test.crm4.dynamics.com";
    private const string UniqueName = "sample_testflow";
    private const string StageA = "11111111-1111-1111-1111-111111110201";
    private const string StageB = "11111111-1111-1111-1111-111111110202";
    private const string Instance = "11111111-1111-1111-1111-111111110301";
    private static readonly Guid Record = Guid.Parse("11111111-1111-1111-1111-111111110401");

    private FakeDataverse _dataverse = null!;
    private BusinessProcessFlowService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        _dataverse = new FakeDataverse();
        var tokens = new Mock<ITokenProvider>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("test-token");

        var client = new DataverseHttpClient(new HttpClient(_dataverse), tokens.Object, NullLogger<DataverseHttpClient>.Instance);
        _svc = new BusinessProcessFlowService(client, NullLogger<BusinessProcessFlowService>.Instance);

        _dataverse.On("GET", "organizations?$select=languagecode", new { value = new[] { new { languagecode = 1033 } } });
        _dataverse.On("GET", "EntityDefinitions(LogicalName='account')/Attributes", new
        {
            value = new object[]
            {
                Attribute("name", "String", "Account Name"),
                Attribute("description", "Memo", "Description"),
                Attribute("industrycode", "Picklist", "Industry"),
                Attribute("numberofemployees", "Integer", "Employees")
            }
        });
        _dataverse.On("GET", "EntityDefinitions(LogicalName='account')?$select=LogicalName,IsBusinessProcessEnabled",
            new { LogicalName = "account", IsBusinessProcessEnabled = true });
    }

    private static object Attribute(string name, string type, string label) => new
    {
        LogicalName = name,
        AttributeType = type,
        DisplayName = new
        {
            LocalizedLabels = new[] { new { Label = label, LanguageCode = 1033 } },
            UserLocalizedLabel = new { Label = label + " (user language)", LanguageCode = 1031 }
        }
    };

    /// <summary>An active process "A → B" on account with an instance table.</summary>
    private void GivenActiveProcess(BpfDefinition? definition = null)
    {
        definition ??= new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages =
            [
                new BpfStage { StageId = StageA, Name = "A", Steps = [new BpfStep { StepId = "11111111-1111-1111-1111-111111110211", Attribute = "name" }] },
                new BpfStage { StageId = StageB, Name = "B", Steps = [new BpfStep { StepId = "11111111-1111-1111-1111-111111110212", Attribute = "description" }] }
            ]
        };

        var xaml = BpfXamlBuilder.Build(definition, BpfTestData.ProcessId).Xaml;
        _dataverse.On("GET", $"workflows({BpfTestData.ProcessId})?$select=", new
        {
            workflowid = BpfTestData.ProcessId,
            name = "Test flow",
            uniquename = UniqueName,
            primaryentity = "account",
            statecode = 1,
            businessprocesstype = 0,
            category = 4,
            ismanaged = false,
            xaml
        });
        _dataverse.On("GET", $"EntityDefinitions(LogicalName='{UniqueName}')?$select=LogicalName,EntitySetName", new
        {
            LogicalName = UniqueName,
            EntitySetName = UniqueName + "s",
            ManyToOneRelationships = new[]
            {
                new { ReferencedEntity = "account", ReferencingAttribute = "bpf_accountid", ReferencingEntityNavigationPropertyName = "bpf_accountid" }
            }
        });
    }

    // ---------------------------------------------------------------- rewriting

    [Test]
    public async Task SetDefinition_RenameWithoutStageId_WithActiveInstances_IsRefused()
    {
        GivenActiveProcess();
        _dataverse.On("GET", $"{UniqueName}s?$select=businessprocessflowinstanceid&$top=5000&$filter=_activestageid_value eq {StageB}",
            new { value = new[] { new { businessprocessflowinstanceid = Instance } } });

        var renamed = new BpfDefinition
        {
            Stages =
            [
                new BpfStage { Name = "A", Steps = [new BpfStep { Attribute = "name" }] },
                new BpfStage { Name = "B renamed", Steps = [new BpfStep { Attribute = "description" }] }
            ]
        };

        var result = await _svc.SetDefinitionAsync(OrgUrl, BpfTestData.ProcessId, renamed);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.False);
            Assert.That(result.Validation.Issues.Single(i => i.Code == "BPF060").Severity, Is.EqualTo("error"));
            Assert.That(result.Validation.Issues.Single(i => i.Code == "BPF062").Fix, Does.Contain(StageB));
            Assert.That(_dataverse.Requests.Any(r => r.Method == "PATCH"), Is.False, "nothing may be written");
        });
    }

    [Test]
    public async Task SetDefinition_RenameWithoutStageId_AllowStageRemoval_WritesWithWarning()
    {
        GivenActiveProcess();
        _dataverse.On("GET", $"{UniqueName}s?$select=businessprocessflowinstanceid",
            new { value = new[] { new { businessprocessflowinstanceid = Instance } } });
        _dataverse.On("PATCH", $"workflows({BpfTestData.ProcessId})", null, HttpStatusCode.NoContent);

        var renamed = new BpfDefinition
        {
            Stages =
            [
                new BpfStage { Name = "A", Steps = [new BpfStep { Attribute = "name" }] },
                new BpfStage { Name = "B renamed", Steps = [new BpfStep { Attribute = "description" }] }
            ]
        };

        var result = await _svc.SetDefinitionAsync(OrgUrl, BpfTestData.ProcessId, renamed, allowStageRemoval: true);

        Assert.That(result.Applied, Is.True);
        Assert.That(result.Validation.Issues.Single(i => i.Code == "BPF060").Severity, Is.EqualTo("warning"));
    }

    [Test]
    public async Task SetDefinition_DryRun_WritesNothing_ListsChanges_AndLeavesNewIdsOpen()
    {
        GivenActiveProcess();

        var changed = new BpfDefinition
        {
            Stages =
            [
                new BpfStage { StageId = StageA, Name = "A", Steps = [new BpfStep { Attribute = "name", Required = true }] },
                new BpfStage { StageId = StageB, Name = "B", Steps = [new BpfStep { Attribute = "description" }, new BpfStep { Attribute = "numberofemployees" }] },
                new BpfStage { Name = "C", Steps = [new BpfStep { Attribute = "industrycode" }] }
            ]
        };

        var result = await _svc.SetDefinitionAsync(OrgUrl, BpfTestData.ProcessId, changed, dryRun: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.False);
            Assert.That(result.Backup, Is.Null, "a dry run hands out no backup");
            Assert.That(_dataverse.Requests.Any(r => r.Method == "PATCH"), Is.False);
            Assert.That(result.Stages[2].StageId, Is.Null, "a new stage gets its id on the real write");
            Assert.That(result.Stages[1].StepIds[1], Is.Null);
            Assert.That(result.Stages[1].StepIds[0], Is.EqualTo("11111111-1111-1111-1111-111111110212"));
            Assert.That(result.Diff, Has.Some.Contains("+ stage 'C'"));
            Assert.That(result.Diff, Has.Some.Contains("step name now required"));
            Assert.That(result.Diff, Has.Some.Contains("+ stage 'B': step numberofemployees"));
            Assert.That(result.Diff, Has.Some.Contains("next end → C"));
        });
    }

    // ---------------------------------------------------------------- literals

    [Test]
    public async Task Validate_ChoiceComparedWithLabel_IsAnError_ThatNamesTheOption()
    {
        _dataverse.On("GET", "Attributes(LogicalName='industrycode')/Microsoft.Dynamics.CRM.PicklistAttributeMetadata", new
        {
            LogicalName = "industrycode",
            OptionSet = new
            {
                Options = new[]
                {
                    new { Value = 1, Label = new { UserLocalizedLabel = new { Label = "Accounting" } } },
                    new { Value = 5, Label = new { UserLocalizedLabel = new { Label = "Software" } } }
                }
            }
        });

        var (result, _, _) = await _svc.ValidateAsync(OrgUrl, Branching(new WorkflowValue { Literal = "Software" }));

        var issue = result.Issues.Single(i => i.Code == "BPF306");
        Assert.That(issue.Severity, Is.EqualTo("error"));
        Assert.That(issue.Fix, Does.Contain("Did you mean 5 (Software)"));
    }

    [Test]
    public async Task Validate_LiteralWithoutDataType_GetsTheColumnsType()
    {
        _dataverse.On("GET", "Microsoft.Dynamics.CRM.PicklistAttributeMetadata", new
        {
            OptionSet = new { Options = new[] { new { Value = 5, Label = new { UserLocalizedLabel = new { Label = "Software" } } } } }
        });

        var (result, completed, _) = await _svc.ValidateAsync(OrgUrl, Branching(new WorkflowValue { Literal = "5" }));

        Assert.That(result.CanSave, Is.True, string.Join("; ", result.Issues.Select(i => i.Problem)));
        Assert.That(completed.Stages[0].Branch!.Branches[0].Conditions[0].Value!.DataType, Is.EqualTo("OptionSetValue"));
    }

    [Test]
    public async Task Validate_DeclaredTypeOtherThanTheColumns_IsAnError()
    {
        var (result, _, _) = await _svc.ValidateAsync(OrgUrl, Branching(new WorkflowValue { DataType = "String", Literal = "5" }));

        Assert.That(result.Issues.Single(i => i.Code == "BPF306").Path, Does.EndWith(".value.dataType"));
    }

    private static BpfDefinition Branching(WorkflowValue value) => new()
    {
        PrimaryEntity = "account",
        Stages =
        [
            new BpfStage
            {
                Name = "A",
                Steps = [new BpfStep { Attribute = "industrycode", Required = true }],
                Branch = new BpfBranching
                {
                    Branches = [new BpfBranch { Conditions = [new WorkflowCondition { Attribute = "industrycode", Operator = "Equal", Value = value }], Next = "C" }],
                    Else = "B"
                }
            },
            new BpfStage { Name = "B", Steps = [new BpfStep { Attribute = "name" }], Next = "end" },
            new BpfStage { Name = "C", Steps = [new BpfStep { Attribute = "description" }] }
        ]
    };

    [Test]
    public void Validate_MetadataError_OtherThanNotFound_IsNotReportedAsMissingTable()
    {
        _dataverse.On("GET", "EntityDefinitions(LogicalName='contact')", new { error = new { message = "throttled" } },
            HttpStatusCode.ServiceUnavailable);

        var definition = new BpfDefinition { PrimaryEntity = "contact", Stages = [new BpfStage { Name = "A", Steps = [new BpfStep { Attribute = "lastname" }] }] };

        Assert.ThrowsAsync<HttpRequestException>(() => _svc.ValidateAsync(OrgUrl, definition));
    }

    // ---------------------------------------------------------------- create

    [Test]
    public async Task Create_ActivationFails_KeepsTheDraftAndReportsItsId()
    {
        _dataverse.On("GET", "workflows?$select=workflowid,name&$filter=uniquename", new { value = Array.Empty<object>() });
        _dataverse.On("GET", "EntityDefinitions(LogicalName='sample_newflow')", new { error = new { message = "not found" } }, HttpStatusCode.NotFound);
        _dataverse.On("POST", "workflows", null, HttpStatusCode.NoContent);
        _dataverse.On("PATCH", "workflows(", new { error = new { message = "activation broke" } }, HttpStatusCode.BadRequest);

        var definition = new BpfDefinition { PrimaryEntity = "account", Stages = [new BpfStage { Name = "A", Steps = [new BpfStep { Attribute = "name" }] }] };

        var (result, _) = await _svc.CreateAsync(OrgUrl, "New flow", definition, "sample_newflow", activate: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.True);
            Assert.That(result.ProcessId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(result.Message, Does.Contain("activation failed").And.Contain("bpf_set_state"));
        });
    }

    // ---------------------------------------------------------------- instances

    [Test]
    public async Task StartInstance_RecordAlreadyHasOne_ReturnsItWithoutCreating()
    {
        GivenActiveProcess();
        _dataverse.On("GET", $"{UniqueName}s?$select=businessprocessflowinstanceid,statuscode,_activestageid_value&$filter=_bpf_accountid_value eq {Record}",
            new { value = new[] { new { businessprocessflowinstanceid = Instance, statuscode = 1, _activestageid_value = StageB } } });
        _dataverse.On("GET", $"processstages({StageB})", new { stagename = "B" });

        var result = await _svc.StartInstanceAsync(OrgUrl, BpfTestData.ProcessId, Record);

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Is.False);
            Assert.That(result.InstanceId, Is.EqualTo(Guid.Parse(Instance)));
            Assert.That(result.Message, Does.Contain("stage 'B'"));
            Assert.That(_dataverse.Requests.Any(r => r.Method == "POST"), Is.False);
        });
    }

    [Test]
    public void SetInstanceStatus_FinishedBeforeTheLastStage_IsRefusedWithAReason()
    {
        GivenActiveProcess();
        _dataverse.On("GET", $"{UniqueName}s({Instance})?$select=_activestageid_value", new { _activestageid_value = StageA });

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SetInstanceStatusAsync(OrgUrl, BpfTestData.ProcessId, Guid.Parse(Instance), "finished"));

        Assert.That(ex!.Message, Does.Contain("'A'").And.Contain("last stage"));
        Assert.That(_dataverse.Requests.Any(r => r.Method == "PATCH"), Is.False);
    }

    [Test]
    public void MoveInstance_FinishedInstance_AsksForReactivation()
    {
        GivenActiveProcess();
        _dataverse.On("GET", $"{UniqueName}s({Instance})?$select=traversedpath", new
        {
            traversedpath = $"{StageA},{StageB}",
            _activestageid_value = StageB,
            statecode = 1,
            statuscode = 2
        });

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.MoveInstanceAsync(OrgUrl, BpfTestData.ProcessId, Guid.Parse(Instance), Guid.Parse(StageA)));

        Assert.That(ex!.Message, Does.Contain("finished").And.Contain("bpf_instance_set_status"));
    }

    // ---------------------------------------------------------------- helpers without I/O

    [TestCase("OptionSetValue", "5", true)]
    [TestCase("OptionSetValue", "Software", false)]
    [TestCase("Boolean", "true", true)]
    [TestCase("Boolean", "1", true)]
    [TestCase("Boolean", "ja", false)]
    [TestCase("EntityReference", "account:11111111-1111-1111-1111-111111110001:Contoso", true)]
    [TestCase("EntityReference", "Contoso", false)]
    [TestCase("Money", "10000.50", true)]
    [TestCase("Integer", "10.5", false)]
    [TestCase("DateTime", "2024-01-31", true)]
    public void LiteralFits(string dataType, string literal, bool fits) =>
        Assert.That(BusinessProcessFlowService.LiteralFits(dataType, literal), Is.EqualTo(fits));

    [TestCase("""{"properties":{"definition":{"triggers":{"manual":{"type":"Request","kind":"Button"}}}}}""", "Request/Button", false)]
    [TestCase("""{"properties":{"definition":{"triggers":{"t":{"type":"Recurrence"}}}}}""", "Recurrence", true)]
    [TestCase("""{"properties":{"definition":{"triggers":{"t":{"type":"OpenApiConnectionWebhook","inputs":{"host":{"operationId":"SubscribeWebhookTrigger"}}}}}}}""", "OpenApiConnectionWebhook/SubscribeWebhookTrigger", true)]
    public void FlowTrigger(string clientData, string trigger, bool automated)
    {
        Assert.That(BusinessProcessFlowService.FlowTriggerOf(clientData), Is.EqualTo(trigger));
        Assert.That(BusinessProcessFlowService.IsAutomatedTrigger(trigger), Is.EqualTo(automated));
    }

    /// <summary>A scripted Dataverse: first registered route whose method and URL fragment match answers.</summary>
    private sealed class FakeDataverse : HttpMessageHandler
    {
        private readonly List<(string Method, string Fragment, object? Body, HttpStatusCode Status)> _routes = [];

        public List<(string Method, string Url)> Requests { get; } = [];

        /// <summary>Later registrations win over earlier ones, so a test can override the defaults.</summary>
        public void On(string method, string urlFragment, object? body, HttpStatusCode status = HttpStatusCode.OK) =>
            _routes.Insert(0, (method, urlFragment, body, status));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add((request.Method.Method, url));

            var route = _routes.FirstOrDefault(r => r.Method == request.Method.Method && url.Contains(r.Fragment, StringComparison.Ordinal));
            if (route.Method is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"{{\"error\":{{\"message\":\"no route for {request.Method} {url}\"}}}}", Encoding.UTF8, "application/json")
                });

            var response = new HttpResponseMessage(route.Status);
            if (route.Body is not null)
                response.Content = new StringContent(JsonSerializer.Serialize(route.Body), Encoding.UTF8, "application/json");
            if (request.Method == HttpMethod.Post)
                response.Headers.Add("OData-EntityId", $"{OrgUrl}/api/data/v9.2/workflows({Guid.NewGuid()})");
            return Task.FromResult(response);
        }
    }
}
