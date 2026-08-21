using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Docker;

internal sealed partial class DockerApi
{
    /// <summary>列出卷。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>卷列表与原始结果。</returns>
    public async Task<(IReadOnlyList<VolumeItem> Items, ExecResult Result)> ListVolumesAsync(CancellationToken cancellationToken)
    {
        var result = await Engine.RunAsync($"{D} volume ls --format '{{{{json .}}}}'", null, cancellationToken).ConfigureAwait(false);
        List<VolumeItem> items = [];
        foreach (var row in DockerJson.ParseLines(result.Output))
        {
            items.Add(new()
            {
                Name = DockerJson.Str(row, "Name"),
                Driver = DockerJson.Str(row, "Driver"),
                Scope = DockerJson.Str(row, "Scope"),
                Mountpoint = DockerJson.Str(row, "Mountpoint"),
                Size = DockerJson.Str(row, "Size"),
                Links = DockerJson.Str(row, "Links"),
                Labels = DockerJson.Str(row, "Labels")
            });
        }
        return (items, result);
    }

    /// <summary>建一个卷。</summary>
    /// <param name="name">卷名。</param>
    /// <param name="driver">驱动;为空用 local。</param>
    /// <param name="options">驱动选项,一行一条 <c>key=value</c>。</param>
    /// <param name="labels">标签,一行一条 <c>key=value</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public Task<ExecResult> CreateVolumeAsync(
        string name, string driver, string options, string labels, CancellationToken cancellationToken)
    {
        var command = $"{D} volume create";
        if (driver.Length > 0)
        {
            command += $" -d {Sh.Quote(driver)}";
        }
        foreach (var option in SplitLines(options))
        {
            command += $" -o {Sh.Quote(option)}";
        }
        foreach (var label in SplitLines(labels))
        {
            command += $" --label {Sh.Quote(label)}";
        }
        if (name.Length > 0)
        {
            command += $" {Sh.Quote(name)}";
        }
        return Engine.RunAsync(command, null, cancellationToken);
    }

    /// <summary>删卷。</summary>
    /// <param name="names">卷名。</param>
    /// <param name="force">强制(<c>-f</c>)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐个卷的结果。</returns>
    public Task<IReadOnlyList<BatchOutcome>> RemoveVolumesAsync(
        IReadOnlyList<string> names, bool force, CancellationToken cancellationToken) =>
        RunBatchAsync(names, all => $"{D} volume rm{(force ? " -f" : "")} {Sh.QuoteAll(all)}", LifecycleTimeout, cancellationToken);

    /// <summary>卷详情。</summary>
    /// <param name="name">卷名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>格式化后的 JSON,或错误文本。</returns>
    public async Task<string> InspectVolumeAsync(string name, CancellationToken cancellationToken)
    {
        var result = await Engine.RunAsync($"{D} volume inspect {Sh.Quote(name)}", null, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? DockerJson.Pretty(result.Output) : result.Output;
    }

    /// <summary>列出网络。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>网络列表与原始结果。</returns>
    public async Task<(IReadOnlyList<NetworkItem> Items, ExecResult Result)> ListNetworksAsync(CancellationToken cancellationToken)
    {
        var result = await Engine
                                    .RunAsync($"{D} network ls --no-trunc --format '{{{{json .}}}}'", null, cancellationToken)
                                    .ConfigureAwait(false);
        List<NetworkItem> items = [];
        foreach (var row in DockerJson.ParseLines(result.Output))
        {
            items.Add(new()
            {
                Id = DockerJson.Str(row, "ID"),
                Name = DockerJson.Str(row, "Name"),
                Driver = DockerJson.Str(row, "Driver"),
                Scope = DockerJson.Str(row, "Scope"),
                Internal = DockerJson.Str(row, "Internal"),
                IPv6 = DockerJson.Str(row, "IPv6"),
                CreatedAt = DockerJson.Str(row, "CreatedAt"),
                Labels = DockerJson.Str(row, "Labels")
            });
        }
        return (items, result);
    }

    /// <summary>建一张网络。</summary>
    /// <param name="name">网络名。</param>
    /// <param name="driver">驱动(bridge / overlay / macvlan…);为空用 bridge。</param>
    /// <param name="subnet">子网 CIDR;为空让 docker 自选。</param>
    /// <param name="gateway">网关;为空让 docker 自选。</param>
    /// <param name="isInternal">内部网络(不通外网)。</param>
    /// <param name="ipv6">启用 IPv6。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public Task<ExecResult> CreateNetworkAsync(
        string name, string driver, string subnet, string gateway, bool isInternal, bool ipv6, CancellationToken cancellationToken)
    {
        var command = $"{D} network create";
        if (driver.Length > 0)
        {
            command += $" -d {Sh.Quote(driver)}";
        }
        if (subnet.Length > 0)
        {
            command += $" --subnet {Sh.Quote(subnet)}";
        }
        if (gateway.Length > 0)
        {
            command += $" --gateway {Sh.Quote(gateway)}";
        }
        if (isInternal)
        {
            command += " --internal";
        }
        if (ipv6)
        {
            command += " --ipv6";
        }
        command += $" {Sh.Quote(name)}";
        return Engine.RunAsync(command, null, cancellationToken);
    }

    /// <summary>删网络。</summary>
    /// <param name="names">网络名或 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐个网络的结果。</returns>
    public Task<IReadOnlyList<BatchOutcome>> RemoveNetworksAsync(IReadOnlyList<string> names, CancellationToken cancellationToken) =>
        RunBatchAsync(names, all => $"{D} network rm {Sh.QuoteAll(all)}", LifecycleTimeout, cancellationToken);

    /// <summary>网络详情。</summary>
    /// <param name="name">网络名或 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>格式化后的 JSON,或错误文本。</returns>
    public async Task<string> InspectNetworkAsync(string name, CancellationToken cancellationToken)
    {
        var result = await Engine.RunAsync($"{D} network inspect {Sh.Quote(name)}", null, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? DockerJson.Pretty(result.Output) : result.Output;
    }

    /// <summary>把容器接进网络。</summary>
    /// <param name="network">网络名或 id。</param>
    /// <param name="container">容器名或 id。</param>
    /// <param name="alias">网络内别名;为空不设。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public Task<ExecResult> ConnectNetworkAsync(string network, string container, string alias, CancellationToken cancellationToken)
    {
        var command = $"{D} network connect";
        if (alias.Length > 0)
        {
            command += $" --alias {Sh.Quote(alias)}";
        }
        command += $" {Sh.Quote(network)} {Sh.Quote(container)}";
        return Engine.RunAsync(command, null, cancellationToken);
    }

    /// <summary>把容器从网络里摘掉。</summary>
    /// <param name="network">网络名或 id。</param>
    /// <param name="container">容器名或 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public Task<ExecResult> DisconnectNetworkAsync(string network, string container, CancellationToken cancellationToken) =>
        Engine.RunAsync($"{D} network disconnect -f {Sh.Quote(network)} {Sh.Quote(container)}", null, cancellationToken);
}
