using Alife.Framework;
using Microsoft.AspNetCore.Components.Rendering;

namespace Suran.QQMedia;

/// <summary>QQ音视频发送 的设置页。内容由 SuranUiKit 按配置项自动分组生成。</summary>
public sealed class QQMediaUI : ModuleUIBase<QQMediaModule, QQMediaConfig>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "配置未加载");
            return;
        }
        SuranUiKit.Render(this, StateHasChanged, b, Configuration, "QQ音视频发送", "把 B站 / YouTube 的音视频下载成文件，以 QQ 语音条和视频消息发到当前会话", "#10b981");
    }
}
