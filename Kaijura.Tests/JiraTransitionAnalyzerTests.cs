using Kaijura.App.Models;
using Kaijura.App.Services;

namespace Kaijura.Tests;

public sealed class JiraTransitionAnalyzerTests
{
    [Fact]
    public void BuildOptionsClassifiesDirectFormAndUnsupportedTransitions()
    {
        var options = new JiraTransitionAnalyzer().BuildOptions(
        [
            Transition("11", "Start Progress", "In Progress"),
            Transition("21", "Resolve", "Done", Field("comment", "Comment", "comment", "comment", "", "add")),
            Transition("31", "Close", "Closed", Field("resolution", "Resolution", "resolution", "resolution", "", "set")),
            Transition("41", "Force Close", "Closed")
        ]);

        var direct = Assert.Single(options, option => option.Id == "11");
        Assert.True(direct.IsEnabled);
        Assert.False(direct.RequiresForm);
        Assert.Equal("In Progress", direct.Label);

        var withComment = Assert.Single(options, option => option.Id == "21");
        Assert.True(withComment.IsEnabled);
        Assert.True(withComment.RequiresForm);
        Assert.True(withComment.RequiresComment);
        Assert.Equal("Done", withComment.Label);

        var unsupported = Assert.Single(options, option => option.Id == "31");
        Assert.False(unsupported.IsEnabled);
        Assert.Contains("Resolution", unsupported.DisabledReason);
        Assert.Equal("Closed (Close)", unsupported.Label);

        var duplicate = Assert.Single(options, option => option.Id == "41");
        Assert.Equal("Closed (Force Close)", duplicate.Label);
    }

    [Fact]
    public void BuildOptionsSupportsWorklogAndRequiredTextFields()
    {
        var options = new JiraTransitionAnalyzer().BuildOptions(
        [
            Transition(
                "51",
                "Block",
                "On Hold",
                Field("worklog", "Log Work", "array", "worklog", "worklog", "add"),
                Field("customfield_12345", "Block Comment", "string", "", "", "set"),
                Field("customfield_67890", "Root Cause", "text", "", "", "set"))
        ]);

        var option = Assert.Single(options);
        Assert.True(option.IsEnabled);
        Assert.True(option.RequiresForm);
        Assert.True(option.RequiresWorklog);
        Assert.Collection(
            option.RequiredTextFields,
            field =>
            {
                Assert.Equal("customfield_12345", field.Id);
                Assert.Equal("Block Comment", field.Name);
            },
            field =>
            {
                Assert.Equal("customfield_67890", field.Id);
                Assert.Equal("Root Cause", field.Name);
            });
    }

    private static JiraTransition Transition(string id, string name, string toStatus, params JiraTransitionField[] fields)
    {
        return new JiraTransition(id, name, toStatus, fields);
    }

    private static JiraTransitionField Field(
        string id,
        string name,
        string schemaType,
        string schemaSystem,
        string schemaItems,
        params string[] operations)
    {
        return new JiraTransitionField(id, name, Required: true, schemaType, schemaSystem, schemaItems, operations);
    }
}
