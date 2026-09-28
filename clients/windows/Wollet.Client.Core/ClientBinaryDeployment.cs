namespace Wollet.Client.Core;

public static class ClientBinaryDeployment
{
    public static async Task DeployAsync(
        string source, string destination,
        Func<CancellationToken, Task> stop,
        Func<CancellationToken, Task> configureAndStart,
        Func<CancellationToken, Task> restoreService,
        CancellationToken cancellationToken)
    {
        var samePath = string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);
        var staged = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        var backup = destination + ".backup-" + Guid.NewGuid().ToString("N");
        var backedUp = false;
        var replaced = false;
        var serviceTouched = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Finish copying before interrupting the service. Staging shares the destination ACL.
            if (!samePath) File.Copy(source, staged);
            serviceTouched = true;
            await stop(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!samePath)
            {
                if (File.Exists(destination))
                {
                    File.Move(destination, backup);
                    backedUp = true;
                }
                File.Move(staged, destination);
                replaced = true;
            }
            await configureAndStart(cancellationToken);
        }
        catch (Exception failure)
        {
            if (serviceTouched)
            {
                try
                {
                    // Recovery must also run when the UI lifetime token has been cancelled.
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await stop(recovery.Token);
                    if (replaced) File.Delete(destination);
                    if (backedUp) File.Move(backup, destination);
                    await restoreService(recovery.Token);
                }
                catch (Exception recoveryFailure)
                {
                    throw new AggregateException(
                        $"更新失败，自动恢复未完成。设备配置已保留；请重新运行发布文件修复。旧程序备份（若已创建）：{backup}",
                        failure, recoveryFailure);
                }
                throw new InvalidOperationException("更新失败，已恢复原程序和服务状态，设备配置保持不变。请关闭其他客户端窗口后重试。", failure);
            }
            throw;
        }
        finally
        {
            // Cleanup errors must not turn a successful deployment into an apparent failure.
            TryDelete(staged);
        }
        TryDelete(backup);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
