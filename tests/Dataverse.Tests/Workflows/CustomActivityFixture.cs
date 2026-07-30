namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Models;
using Dataverse.Core.Workflows;

/// <summary>
/// The parameter metadata of <c>msdyncrmWorkflowTools.CheckUserInTeam</c>, copied verbatim from
/// <c>plugintype.customworkflowactivityinfo</c> in contoso-dev.
/// </summary>
/// <remarks>
/// Built by running the real parser over the real blob, so builder and validator tests exercise the
/// same shape they see in production — including the CRM-side parameter types
/// (<c>Microsoft.Crm.Sdk.Lookup</c>, <c>CrmBoolean</c>) and the <c>EntityNames</c> of a lookup.
/// </remarks>
internal static class CustomActivityFixture
{
    public const string CheckUserInTeam =
        "msdyncrmWorkflowTools.CheckUserInTeam, msdyncrmWorkflowTools, Version=1.0.62.1, " +
        "Culture=neutral, PublicKeyToken=416e876b9bee261e";

    private const string CheckUserInTeamInfo = """
        <?xml version="1.0" encoding="utf-16"?>
        <SandboxCustomActivityInfo>
          <CustomActivityInfo>
            <Name>msdyncrmWorkflowTools.CheckUserInTeam</Name>
            <GroupName>msdyncrmWorkflowTools (1.0.62.1)</GroupName>
            <TypeName>msdyncrmWorkflowTools.CheckUserInTeam</TypeName>
            <AssemblyName>msdyncrmWorkflowTools</AssemblyName>
            <PublicKeyToken>416e876b9bee261e</PublicKeyToken>
            <Culture>neutral</Culture>
            <AssemblyVersion>1.0.62.1</AssemblyVersion>
          </CustomActivityInfo>
          <Inputs>
            <CustomActivityParameterInfo>
              <Name>Team</Name>
              <TypeName>Microsoft.Crm.Sdk.Lookup, Microsoft.Crm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35</TypeName>
              <WorkflowAttributeType>Boolean</WorkflowAttributeType>
              <Required>true</Required>
              <DependencyPropertyName>Team</DependencyPropertyName>
              <EntityNames>
                <string>team</string>
              </EntityNames>
            </CustomActivityParameterInfo>
            <CustomActivityParameterInfo>
              <Name>User</Name>
              <TypeName>Microsoft.Crm.Sdk.Lookup, Microsoft.Crm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35</TypeName>
              <WorkflowAttributeType>Boolean</WorkflowAttributeType>
              <Required>false</Required>
              <DependencyPropertyName>User</DependencyPropertyName>
              <EntityNames>
                <string>systemuser</string>
              </EntityNames>
            </CustomActivityParameterInfo>
          </Inputs>
          <Outputs>
            <CustomActivityParameterInfo>
              <Name>isUserInTeam</Name>
              <TypeName>Microsoft.Crm.Sdk.CrmBoolean, Microsoft.Crm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35</TypeName>
              <WorkflowAttributeType>Boolean</WorkflowAttributeType>
              <Required>false</Required>
              <DependencyPropertyName>isUserInTeam</DependencyPropertyName>
              <EntityNames />
            </CustomActivityParameterInfo>
          </Outputs>
          <AssemblyQualifiedName>msdyncrmWorkflowTools.CheckUserInTeam, msdyncrmWorkflowTools, Version=1.0.62.1, Culture=neutral, PublicKeyToken=416e876b9bee261e</AssemblyQualifiedName>
          <ValidationError />
        </SandboxCustomActivityInfo>
        """;

    public static IReadOnlyList<WorkflowActivityParameter> Parameters =>
        CustomActivityInfoParser.Parse(CheckUserInTeamInfo)!.Parameters;

    public static WorkflowActivityCatalog Catalog() =>
        new([new(CheckUserInTeam, Parameters)]);
}
