namespace VwapScanner.Mobile;

/// <summary>One line of a result list: a bold title and a smaller detail text; IsNew marks a signal not seen in an earlier scan.</summary>
public sealed record Row(string Title, string Detail, bool IsNew = false);
