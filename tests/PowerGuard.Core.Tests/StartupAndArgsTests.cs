using System.Xml.Linq;
using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

public sealed class FakeRunner : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args, string? XmlContent)> Calls { get; } = new();
    public Func<IReadOnlyList<string>, ProcessResult> Handler { get; set; } = _ => new(0, "", "");
    public Task<ProcessResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken ct = default)
    {
        var i = a.ToList().IndexOf("/XML");
        string? xml = i >= 0 && i + 1 < a.Count && File.Exists(a[i + 1]) ? File.ReadAllText(a[i + 1]) : null;
        Calls.Add((f, a, xml));
        return Task.FromResult(Handler(a));
    }
}

public class StartupAndArgsTests
{
    [Theory]
    [InlineData(DisplayMode.Internal, "/internal")]
    [InlineData(DisplayMode.External, "/external")]
    [InlineData(DisplayMode.Extend, "/extend")]
    [InlineData(DisplayMode.Clone, "/clone")]
    public void DisplaySwitchArgs_Mapping(DisplayMode m, string arg) => Assert.Equal(arg, DisplaySwitchArgs.For(m));

    [Theory]
    [InlineData(DisplayMode.Internal, "1")]
    [InlineData(DisplayMode.Clone, "2")]
    [InlineData(DisplayMode.Extend, "3")]
    [InlineData(DisplayMode.External, "4")]
    public void DisplaySwitchArgs_NumericMapping(DisplayMode m, string arg) => Assert.Equal(arg, DisplaySwitchArgs.For(m, DisplaySwitchArgStyle.Numeric));

    [Theory]
    [InlineData(19045, DisplaySwitchArgStyle.Auto, DisplaySwitchArgStyle.Slash)]
    [InlineData(22631, DisplaySwitchArgStyle.Auto, DisplaySwitchArgStyle.Numeric)]
    [InlineData(26100, DisplaySwitchArgStyle.Slash, DisplaySwitchArgStyle.Slash)]
    public void DisplaySwitchArgs_Resolve(int build, DisplaySwitchArgStyle s, DisplaySwitchArgStyle expected) =>
        Assert.Equal(expected, DisplaySwitchArgs.Resolve(s, build));

    [Fact]
    public void DisplaySwitchArgs_UnknownThrows() => Assert.Throws<ArgumentOutOfRangeException>(() => DisplaySwitchArgs.For(DisplayMode.Unknown));

    private const string Exe = @"C:\Program Files\PowerGuard Display\PowerGuardDisplay.exe";

    [Fact] // 用例 15、16
    public void TaskXml_CurrentUserLogon_NoAdmin_BatteryAllowed_BoundedRestart()
    {
        var xml = StartupTaskService.BuildTaskXml(Exe, @"PC\runa");
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(@"PC\runa", doc.Descendants(ns + "LogonTrigger").Elements(ns + "UserId").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("IgnoreNew", doc.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("3", doc.Descendants(ns + "RestartOnFailure").Elements(ns + "Count").Single().Value);
        Assert.Equal(Exe, doc.Descendants(ns + "Command").Single().Value); // 含空格的路径作为独立元素，无需引号拼接
        Assert.Equal("--autostart", doc.Descendants(ns + "Arguments").Single().Value);
    }

    [Fact]
    public void TaskXml_EscapesSpecialCharacters()
    {
        var xml = StartupTaskService.BuildTaskXml(@"C:\A&B <x>\app.exe", "u");
        Assert.Contains("A&amp;B &lt;x&gt;", xml);
    }

    [Fact] // 用例 15：启用
    public async Task Enable_CallsSchtasksCreateWithXmlFile()
    {
        var r = new FakeRunner();
        var svc = new StartupTaskService(r, @"C:\Windows\System32\schtasks.exe");
        var res = await svc.EnableAsync(Exe, "u");
        Assert.True(res.Success);
        var call = Assert.Single(r.Calls);
        Assert.Equal(["/Create", "/TN", "PowerGuardDisplay", "/XML"], call.Args.Take(4));
        Assert.Equal("/F", call.Args.Last());
        Assert.Contains("LogonTrigger", call.XmlContent);
        Assert.False(File.Exists(call.Args[4])); // 临时 XML 已删除
    }

    [Fact]
    public async Task Enable_Failure_ReturnsErrorAndManualHelp()
    {
        var r = new FakeRunner { Handler = _ => new(1, "", "错误: 拒绝访问。") };
        var res = await new StartupTaskService(r, "schtasks").EnableAsync(Exe, "u");
        Assert.False(res.Success);
        Assert.Contains("拒绝访问", res.Message);
        Assert.Contains("taskschd.msc", res.NextStep);
    }

    [Fact] // 用例 15：禁用
    public async Task Disable_DeletesTask_AndMissingTaskIsOk()
    {
        var r = new FakeRunner { Handler = a => a[0] == "/Delete" ? new(1, "", "not found") : new(1, "", "not found") };
        var res = await new StartupTaskService(r, "schtasks").DisableAsync();
        Assert.True(res.Success);
        Assert.Equal("/Delete", r.Calls[0].Args[0]);
    }

    [Fact]
    public async Task Query_ParsesExistingTask()
    {
        var xml = StartupTaskService.BuildTaskXml(Exe, "u");
        var r = new FakeRunner { Handler = _ => new(0, xml, "") };
        var st = await new StartupTaskService(r, "schtasks").QueryAsync(Exe);
        Assert.True(st.Exists); Assert.True(st.Enabled); Assert.True(st.PointsToCurrentExe);
        var st2 = await new StartupTaskService(r, "schtasks").QueryAsync(@"D:\other\PowerGuardDisplay.exe");
        Assert.False(st2.PointsToCurrentExe);
    }

    [Fact]
    public async Task Query_MissingTask()
    {
        var r = new FakeRunner { Handler = _ => new(1, "", "ERROR") };
        var st = await new StartupTaskService(r, "schtasks").QueryAsync(Exe);
        Assert.False(st.Exists);
        Assert.Null(st.Error);
    }

    [Fact]
    public async Task Query_SchtasksCannotStart_ReportsError()
    {
        var r = new FakeRunner { Handler = _ => new(-1, "", "", "file not found") };
        var st = await new StartupTaskService(r, "schtasks").QueryAsync(Exe);
        Assert.NotNull(st.Error);
    }
}
