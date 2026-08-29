namespace MafDemo.Core.Handbook;

public interface IEmbedder
{
    Task<float[]> EmbedAsync(string text);
}
