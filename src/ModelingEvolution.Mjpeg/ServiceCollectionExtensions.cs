using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ModelingEvolution.Mjpeg;

/// <summary>
/// Extension methods for registering MJPEG services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds MJPEG codec and HDR blending services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMjpeg(this IServiceCollection services)
    {
        return services.AddMjpeg(new JpegCodecOptions());
    }

    /// <summary>
    /// Adds MJPEG codec and HDR blending services with custom options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">JPEG codec configuration options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMjpeg(this IServiceCollection services, JpegCodecOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton<IHdrBlend, HdrBlend>();
        services.TryAddSingleton<IJpegCodec>(_ => new JpegCodec(options));
        services.TryAddSingleton(options);

        return services;
    }

    /// <summary>
    /// Adds MJPEG codec and HDR blending services with options builder.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMjpeg(this IServiceCollection services, Action<JpegCodecOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new JpegCodecOptions();
        configure(options);

        return services.AddMjpeg(options);
    }

    /// <summary>
    /// Adds only the HDR blending service without JPEG codec.
    /// Useful when JPEG codec is not needed or provided separately.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHdrBlend(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHdrBlend, HdrBlend>();

        return services;
    }
}
