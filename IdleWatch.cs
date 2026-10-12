using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Bismarck.IdleWatch;

/// <summary>空闲检测配置：只轮询系统最后一次键鼠输入时间，不挂钩子、不碰输入内容。</summary>
public class IdleWatchConfig
{
    [DisplayName("启用键鼠空闲检测")]
    [Description("轮询系统最后一次键鼠输入时间，长时间无输入时提示 AI：主人可能不在屏幕前；恢复输入时再提示一次")]
    public bool EnableIdleWatch { get; set; } = true;

    [DisplayName("判定离屏的空闲秒数")]
    [Description("键鼠无输入超过该秒数，判定主人可能暂时离开；一旦有输入即判定回来了")]
    public int IdleThresholdSeconds { get; set; } = 120;

    [DisplayName("检查间隔（秒）")]
    [Description("每隔多少秒检查一次键鼠空闲状态")]
    public int CheckIntervalSeconds { get; set; } = 5;

    [DisplayName("离屏提示词")]
    [Description("判定主人离开时推给 AI 的提示")]
    public string IdlePrompt { get; set; } =
        "长官可能暂时离开了屏幕前。你可以先静着，不必主动说话；若有要紧事，记着等他回来再提。";

    [DisplayName("回屏提示词")]
    [Description("判定主人回来时推给 AI 的提示")]
    public string BackPrompt { get; set; } =
        "长官回到屏幕前了。";
}

/// <summary>
/// 空闲检测模块：用 GetLastInputInfo 轮询系统级键鼠空闲时长（全局有效、免钩子、无侵入），
/// 只在「在场/离屏」状态切换时上报一次，避免反复刷屏。
/// </summary>
[Module("空闲检测",
    "轮询系统最后一次键鼠输入时间，长时间无输入时提示 AI：主人可能不在屏幕前；恢复输入时再提示一次。",
    defaultCategory: "Bismarck")]
public class IdleWatch(
    XmlFunctionCaller functionCaller,
    Interactor<IdleWatch> interactor,
    ILogger<IdleWatch> logger) :
    ChatBehaviour,
    IConfigurable<IdleWatchConfig>
{
    public IdleWatchConfig Configuration { get; set; } = null!;

    bool isIdle;
    DateTime nextCheckTime = DateTime.MinValue;

    // ===== AI 手动调用入口 =====

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询当前键鼠已空闲多少秒（用于确认主人是否还在屏幕前）")]
    public Task<string> GetIdleSeconds()
    {
        double idle = GetIdleSecondsCore();
        string state = idle >= Math.Max(10, Configuration.IdleThresholdSeconds) ? "可能离屏" : "在场";
        return Task.FromResult($"键鼠已空闲 {idle:F0} 秒（{state}）。");
    }

    // ===== 生命周期 =====

    protected override Task OnAwake()
    {
        XmlHandler xmlHandler = new(this) {
            Description = "查询当前键鼠已空闲多少秒",
            Explanation = "空闲检测：返回系统最后一次键鼠输入距今的秒数，用于判断主人是否还在屏幕前。"
        };
        functionCaller.RegisterHandler(xmlHandler, DocumentMode.Implicit, DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        nextCheckTime = DateTime.Now;
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        if (Configuration.EnableIdleWatch == false)
            return Task.CompletedTask;

        if (DateTime.Now < nextCheckTime)
            return Task.CompletedTask;

        int interval = Math.Max(1, Configuration.CheckIntervalSeconds);
        nextCheckTime = DateTime.Now.AddSeconds(interval);

        double idle = GetIdleSecondsCore();
        int threshold = Math.Max(10, Configuration.IdleThresholdSeconds);

        if (idle >= threshold && isIdle == false)
        {
            isIdle = true;
            Report(idle, left: true);
        }
        else if (idle < 1 && isIdle)
        {
            isIdle = false;
            Report(idle, left: false);
        }

        return Task.CompletedTask;
    }

    // ===== 上报 =====

    void Report(double idleSeconds, bool left)
    {
        try
        {
            StringBuilder sb = new();
            if (left)
            {
                sb.AppendLine($"(键鼠已 {idleSeconds:F0} 秒无输入)");
                sb.AppendLine();
                sb.AppendLine((Configuration.IdlePrompt ?? "").Trim());
            }
            else
            {
                sb.AppendLine("(键鼠恢复了输入)");
                sb.AppendLine();
                sb.AppendLine((Configuration.BackPrompt ?? "").Trim());
            }
            interactor.Poke(sb.ToString());
        }
        catch (Exception e)
        {
            logger.LogError(e, "空闲检测上报失败");
        }
    }

    double GetIdleSecondsCore()
    {
        try
        {
            LASTINPUTINFO lii = new() { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii) == false)
                return 0;
            ulong tick = GetTickCount64();
            uint elapsed = (uint)tick - lii.dwTime; // 相减自动处理 32 位回绕
            return elapsed / 1000.0;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "读取空闲时间失败");
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    static extern ulong GetTickCount64();
}
