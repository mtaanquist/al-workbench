namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Join row linking a <see cref="RuntimeTemplate"/> to one of the
/// organisation's always-included files. A template only emits the files in
/// this list. A file added on the Always-included files page is put on every
/// live template when it is saved; a template can untick it afterwards.
/// </summary>
public class RuntimeTemplateIncludedFile
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int RuntimeTemplateId { get; set; }
    public RuntimeTemplate? RuntimeTemplate { get; set; }

    public int OrganizationFileId { get; set; }
    public OrganizationFile? OrganizationFile { get; set; }

    /// <summary>Position in the admin's reorderable list inside the template editor.</summary>
    public int Ordering { get; set; }
}
