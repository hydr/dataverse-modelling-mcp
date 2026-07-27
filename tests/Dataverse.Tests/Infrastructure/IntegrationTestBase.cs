namespace Dataverse.Tests.Infrastructure;

using Dataverse.Core.Clients;
using Dataverse.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

public abstract class IntegrationTestBase
{
    protected const string OrgUrl = "https://contoso-dev.crm4.dynamics.com";
    protected const string EnvironmentId = "699d56e2-7f65-ebb0-ae92-2ae5e3a45159";
    protected const string FlowRegion = "europe";

    protected SolutionService SolutionService = null!;
    protected TableService TableService = null!;
    protected ViewService ViewService = null!;
    protected SecurityRoleService SecurityRoleService = null!;
    protected EnvironmentVariableService EnvironmentVariableService = null!;
    protected WorkflowService WorkflowService = null!;
    protected CloudFlowService CloudFlowService = null!;
    protected FlowVersionService FlowVersionService = null!;
    protected WebResourceService WebResourceService = null!;
    protected PublishService PublishService = null!;
    protected CommandService CommandService = null!;

    [OneTimeSetUp]
    public void BaseOneTimeSetUp()
    {
        if (Environment.GetEnvironmentVariable("DATAVERSE_INTEGRATION_TESTS") != "true")
            Assert.Ignore("Integration tests disabled. Set DATAVERSE_INTEGRATION_TESTS=true to run.");

        var tokenProvider = new AzureCliTokenProvider();

        var dvHttpClient = new DataverseHttpClient(
            new HttpClient(),
            tokenProvider,
            NullLogger<DataverseHttpClient>.Instance);

        var paHttpClient = new PowerAutomateHttpClient(
            new HttpClient(),
            tokenProvider,
            NullLogger<PowerAutomateHttpClient>.Instance);

        SolutionService = new SolutionService(dvHttpClient, NullLogger<SolutionService>.Instance);
        TableService = new TableService(dvHttpClient, NullLogger<TableService>.Instance);
        ViewService = new ViewService(dvHttpClient, NullLogger<ViewService>.Instance);
        SecurityRoleService = new SecurityRoleService(dvHttpClient, NullLogger<SecurityRoleService>.Instance);
        EnvironmentVariableService = new EnvironmentVariableService(dvHttpClient, NullLogger<EnvironmentVariableService>.Instance);
        WorkflowService = new WorkflowService(dvHttpClient, NullLogger<WorkflowService>.Instance);
        CloudFlowService = new CloudFlowService(paHttpClient, NullLogger<CloudFlowService>.Instance);
        FlowVersionService = new FlowVersionService(dvHttpClient, NullLogger<FlowVersionService>.Instance);
        WebResourceService = new WebResourceService(dvHttpClient, NullLogger<WebResourceService>.Instance);
        PublishService = new PublishService(dvHttpClient, WebResourceService, NullLogger<PublishService>.Instance);
        CommandService = new CommandService(dvHttpClient, NullLogger<CommandService>.Instance);
    }
}
