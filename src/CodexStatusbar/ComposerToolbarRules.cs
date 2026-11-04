namespace CodexStatusbar;

internal static class ComposerToolbarRules
{
    // A confirmed toolbar without a Context indicator belongs to a chat with no
    // usage yet. Retain the waiting state during incomplete accessibility reads.
    public static bool WaitForUsage(ComposerReferenceSource source, bool wasWaiting) => source switch
    {
        ComposerReferenceSource.UiaModel => true,
        ComposerReferenceSource.UiaContext => false,
        _ => wasWaiting
    };

    // Attachments and permissions expose ExpandCollapse too. Without a Context icon,
    // only a menu in the right-hand half of the composer can be the model selector.
    // Among those candidates the tracker chooses the rightmost menu, independently
    // of translated labels and React-generated AutomationIds.
    public static bool IsModelCandidate(IntRect button, IntRect composer, IntRect context) =>
        !button.IsEmpty &&
        (!context.IsEmpty
            ? button.Left >= context.Right
            : button.Left >= composer.Left + composer.Width / 2);

    public static bool IsLeftClusterControl(string controlType, IntRect rect, IntRect reference,
        int rowTop, int rowBottom) =>
        controlType is "Button" or "Image" &&
        !rect.IsEmpty && rect.Height > 1 &&
        (reference.IsEmpty || rect.Right <= reference.Left) &&
        rect.Bottom > rowTop && rect.Top < rowBottom;
}
