namespace ALDevToolbox.Services;

/// <summary>
/// One email ready to send: the subject and both bodies. <see cref="TextBody"/>
/// is the plain-text alternative; when it is set the message goes out as
/// <c>multipart/alternative</c>, and when it is null as HTML alone, which is
/// what every email was before the shared layout (issue #1028).
/// </summary>
public sealed record EmailContent(string Subject, string HtmlBody, string? TextBody = null);
