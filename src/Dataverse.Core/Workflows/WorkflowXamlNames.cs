namespace Dataverse.Core.Workflows;

using System.Text;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// The fixed set of XML namespaces used by Dataverse Classic Workflow (WF4) XAML, with the
/// exact prefixes the platform emits. Shared by <see cref="WorkflowXamlBuilder"/> and
/// <see cref="WorkflowXamlParser"/>. Prefixes matter: type strings inside attribute values
/// (e.g. <c>x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)"</c>) reference them.
/// </summary>
public static class WorkflowXamlNames
{
    public const string ActivitiesUri = "http://schemas.microsoft.com/netfx/2009/xaml/activities";
    public const string XamlUri = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string MvaUri = "clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string MxsUri = "clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string MxsqUri = "clr-namespace:Microsoft.Xrm.Sdk.Query;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string MxswUri = "clr-namespace:Microsoft.Xrm.Sdk.Workflow;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string MxswaUri = "clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string SUri = "clr-namespace:System;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
    public const string ScgUri = "clr-namespace:System.Collections.Generic;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
    public const string ScoUri = "clr-namespace:System.Collections.ObjectModel;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
    public const string SrsUri = "clr-namespace:System.Runtime.Serialization;assembly=System.Runtime.Serialization, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
    public const string ThisUri = "clr-namespace:";
    public const string McwcUri = "clr-namespace:Microsoft.Crm.Workflow.ClientActivities;assembly=Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";

    public static readonly XNamespace Activities = ActivitiesUri;
    public static readonly XNamespace X = XamlUri;
    public static readonly XNamespace Mva = MvaUri;
    public static readonly XNamespace Mxs = MxsUri;
    public static readonly XNamespace Mxsq = MxsqUri;
    public static readonly XNamespace Mxsw = MxswUri;
    public static readonly XNamespace Mxswa = MxswaUri;
    public static readonly XNamespace S = SUri;
    public static readonly XNamespace Scg = ScgUri;
    public static readonly XNamespace Sco = ScoUri;
    public static readonly XNamespace Srs = SrsUri;
    public static readonly XNamespace This = ThisUri;
    public static readonly XNamespace Mcwc = McwcUri;

    // AssemblyQualifiedNames of the built-in Microsoft.Crm.Workflow activities we emit/recognise.
    public const string CompositeAqn = "Microsoft.Crm.Workflow.Activities.Composite, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string EvaluateExpressionAqn = "Microsoft.Crm.Workflow.Activities.EvaluateExpression, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string ConditionSequenceAqn = "Microsoft.Crm.Workflow.Activities.ConditionSequence, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string ConditionBranchAqn = "Microsoft.Crm.Workflow.Activities.ConditionBranch, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    public const string EvaluateConditionAqn = "Microsoft.Crm.Workflow.Activities.EvaluateCondition, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";

    /// <summary>The class name prefix used for the generated <c>x:Class</c> (followed by the GUID without hyphens).</summary>
    public const string ClassPrefix = "XrmWorkflow";

    public static string ClassName(Guid workflowId) => ClassPrefix + workflowId.ToString("N");

    /// <summary>The all-zero class name used by rollup/calculated FormulaDefinition XAML.</summary>
    public static readonly string ZeroClassName = ClassPrefix + Guid.Empty.ToString("N");

    /// <summary>Serializes a WF4 XAML root element to a string with a UTF-16 XML declaration,
    /// matching what the Dataverse platform emits.</summary>
    public static string Serialize(XElement root)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Indent = false,
            Encoding = Encoding.Unicode
        };
        using var sw = new Utf16StringWriter(sb);
        using (var writer = XmlWriter.Create(sw, settings))
        {
            root.Save(writer);
        }
        return sb.ToString();
    }

    private sealed class Utf16StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => Encoding.Unicode;
    }
}
