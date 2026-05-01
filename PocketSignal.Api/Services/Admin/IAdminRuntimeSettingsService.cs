using PocketSignal.Api.Models.Admin;

namespace PocketSignal.Api.Services.Admin;

public interface IAdminRuntimeSettingsService
{
    Task<AdminRuntimeSettings> GetAsync(
        CancellationToken cancellationToken = default);

    Task<AdminRuntimeSettings> UpdateAsync(
        AdminRuntimeSettingsUpdateRequest request,
        CancellationToken cancellationToken = default);
}