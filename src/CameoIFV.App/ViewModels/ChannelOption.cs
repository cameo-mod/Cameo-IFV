using CameoIFV.Core.Model;

namespace CameoIFV.App.ViewModels;

/// <summary>
/// One selectable feed for a mod: pairs a <see cref="ReleaseSource"/> with a friendly label so the
/// UI can distinguish, e.g., CA "Stable (Inq8/CAmod)" from "Dev (darkademic/CAmod)".
/// </summary>
public sealed record ChannelOption(ReleaseSource Source)
{
    public string Label => $"{Source.Channel} ({Source.Repository})";
}
