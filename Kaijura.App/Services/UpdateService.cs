using Kaijura.App.Models;
using Velopack;
using Velopack.Sources;

namespace Kaijura.App.Services;

public sealed class UpdateService
{
    private UpdateManager? _manager;
    private UpdateInfo? _availableUpdate;
    private VelopackAsset? _pendingRestart;

    public async Task<UpdateStatus> CheckAsync(string repositoryUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return new UpdateStatus { Status = "disabled", Message = "Repositorio de updates no configurado." };
        }

        try
        {
            _manager = new UpdateManager(new GithubSource(repositoryUrl, accessToken: string.Empty, prerelease: false));

            if (!_manager.IsInstalled && !_manager.IsPortable)
            {
                return new UpdateStatus
                {
                    Status = "unavailable",
                    Message = "Updates activos solo al ejecutar una instalación Velopack."
                };
            }

            _pendingRestart = _manager.UpdatePendingRestart;
            if (_pendingRestart is not null)
            {
                return new UpdateStatus
                {
                    Status = "ready",
                    Message = "Actualización descargada. Reinicia para aplicarla.",
                    Version = _pendingRestart.Version.ToString(),
                    Progress = 100,
                    CanInstall = true
                };
            }

            _availableUpdate = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            if (_availableUpdate is null)
            {
                return new UpdateStatus { Status = "idle", Message = "La app está actualizada." };
            }

            return new UpdateStatus
            {
                Status = "available",
                Message = "Hay una nueva versión disponible.",
                Version = _availableUpdate.TargetFullRelease.Version.ToString(),
                CanInstall = true
            };
        }
        catch (Exception ex)
        {
            return new UpdateStatus
            {
                Status = "failed",
                Message = $"No se pudo comprobar actualizaciones: {ex.Message}"
            };
        }
    }

    public async Task<UpdateStatus> InstallAsync(Action<UpdateStatus> reportProgress, CancellationToken cancellationToken)
    {
        if (_manager is null)
        {
            return new UpdateStatus { Status = "failed", Message = "El updater todavía no está inicializado." };
        }

        if (_pendingRestart is not null)
        {
            _manager.ApplyUpdatesAndRestart(_pendingRestart);
            return new UpdateStatus { Status = "applying", Message = "Aplicando actualización." };
        }

        if (_availableUpdate is null)
        {
            return new UpdateStatus { Status = "idle", Message = "No hay actualización pendiente." };
        }

        try
        {
            await _manager.DownloadUpdatesAsync(_availableUpdate, progress =>
            {
                reportProgress(new UpdateStatus
                {
                    Status = "downloading",
                    Message = "Descargando actualización.",
                    Version = _availableUpdate.TargetFullRelease.Version.ToString(),
                    Progress = progress,
                    CanInstall = false
                });
            }, cancellationToken);

            _manager.ApplyUpdatesAndRestart(_availableUpdate);
            return new UpdateStatus { Status = "applying", Message = "Aplicando actualización." };
        }
        catch (Exception ex)
        {
            return new UpdateStatus
            {
                Status = "failed",
                Message = $"No se pudo instalar la actualización: {ex.Message}",
                Version = _availableUpdate.TargetFullRelease.Version.ToString()
            };
        }
    }
}
