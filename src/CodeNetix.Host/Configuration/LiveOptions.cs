using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Configuration;

/// <summary>
/// <see cref="IOptions{TOptions}"/> que devuelve siempre el valor vigente de <see cref="IOptionsMonitor{TOptions}"/>
/// (historia 8.1). En <c>serve</c> lo registra Host para las opciones que dependen de secretos configurables desde el
/// Panel: los módulos siguen pidiendo <c>IOptions&lt;T&gt;</c> y leyendo <c>.Value</c> en cada uso, y tras guardar
/// claves <see cref="PanelSecretsService"/> invalida la caché del monitor para que la siguiente lectura las incluya.
/// </summary>
internal sealed class LiveOptions<TOptions>(IOptionsMonitor<TOptions> monitor) : IOptions<TOptions>
    where TOptions : class
{
    public TOptions Value => monitor.CurrentValue;
}
