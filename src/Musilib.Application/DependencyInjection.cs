using Microsoft.Extensions.DependencyInjection;
using Musilib.Application.Jobs;
using Musilib.Application.Services;

namespace Musilib.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddMusilibApplication(this IServiceCollection services)
    {
        services.AddSingleton<PlaylistImportService>();
        services.AddSingleton<MatchingService>();
        services.AddSingleton<TrackEditingService>();
        services.AddSingleton<TransferService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<LocalLibraryService>();
        services.AddSingleton<DashboardService>();

        services.AddSingleton<IJobHandler, ImportJobHandler>();
        services.AddSingleton<IJobHandler, MatchJobHandler>();
        services.AddSingleton<IJobHandler, ExportJobHandler>();
        services.AddSingleton<IJobHandler, TransferJobHandler>();
        services.AddSingleton<JobEngine>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<JobEngine>());
        return services;
    }
}
