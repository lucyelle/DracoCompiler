using System.Threading;
using System.Threading.Tasks;
using Draco.Lsp.Model;
using Draco.Lsp.Server.Workspace;

namespace Draco.LanguageServer;

internal partial class DracoLanguageServer : IDidDeleteFiles
{
    public FileOperationRegistrationOptions DidDeleteFileRegistrationOptions => new()
    {
        Filters = new FileOperationFilter[]
        {
            new()
            {
                Pattern = new()
                {
                    Glob = this.DocumentSelector[0].Pattern!
                }
            }
        }
    };

    public async Task DidDeleteFilesAsync(DeleteFilesParams param, CancellationToken cancellationToken)
    {
        foreach (var file in param.Files)
        {
            await this.DeleteDocument(DocumentUri.From(file.Uri));
        }
    }
}
