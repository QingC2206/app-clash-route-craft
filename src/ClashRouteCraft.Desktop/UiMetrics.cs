namespace ClashRouteCraft.Desktop;

// Shared dimensions for form hierarchy and density. XAML disclosure tokens mirror these roles.
internal static class UiMetrics
{
    public const double Gap = 8, FieldGap = 4, Inset = 16, DisclosureInset = 30, SectionTitle = 15, FieldTitle = 13, ListMinimum = 48;
    // Proposed type scale: page title 20 / section 15 / field 13 / body 13 / supporting 12 / caption 11.
    public const double PageTitle = 20, Body = 13, Supporting = 12, Caption = 11, RowStandard = 32, RowCompact = 28;
    // Proposed control geometry.
    public const double ControlHeight = 32, InputHeight = 34, ControlRadius = 4, CardRadius = 8, CardPadding = 16, CardPaddingVertical = 13;
    public const double ProbeFlyoutWidth = 360, StatusColumn = 20;
}
