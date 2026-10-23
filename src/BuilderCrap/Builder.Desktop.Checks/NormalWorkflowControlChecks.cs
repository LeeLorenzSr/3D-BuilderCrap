internal static partial class Program
{
    // Release smoke checks only: no Narrator, contrast, DPI or accessibility test entry points.
    private static async Task RunNormalWorkflowControls()
    {
        await V1TransformKeys();
        await V1ConstructionTextKeys();
        await V1OperationDrafts();
        await V1NativePromptGuard();
        await RunDocumentNavigationControls();
        await RunStlImportControls();
    }
}
