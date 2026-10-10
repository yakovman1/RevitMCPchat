using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace RevitMcpChat.RevitLogic.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void ManifestNamesExternalApplicationAndStablePane()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RevitMcpChat.addin.template");
        Assert.True(File.Exists(path), $"Revit add-in manifest template was not found at {path}.");

        var document = XDocument.Load(path);
        var application = document.Root?
            .Elements("AddIn")
            .SingleOrDefault(element => (string?)element.Attribute("Type") == "Application");

        Assert.NotNull(application);
        Assert.Equal("RevitMCPchat.RevitApp", (string?)application!.Element("FullClassName"));
        Assert.False(string.IsNullOrWhiteSpace((string?)application.Element("Assembly")));
        Assert.True(Guid.TryParse((string?)application.Element("AddInId"), out var addInId));
        Assert.Equal(Guid.Parse("5c3bd392-7fa5-4976-a889-2fe0cbd531b2"), addInId);
    }
}
