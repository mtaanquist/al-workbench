namespace ALDevToolbox.Services.Cal;

/// <summary>
/// Allow-lists of built-in C/AL names the call-site walker
/// (<see cref="CalReferenceExtractor"/>) must skip, so they don't pollute the
/// reference graph or the unresolved-diagnostic. The C/AL analogue of
/// <c>AlBuiltinMethods</c> — separate because C/AL's runtime surface differs
/// (uppercase idiom, <c>FIND('-')</c>, <c>CALCFIELDS</c>, the classic
/// <c>DATABASE::</c>/<c>CODEUNIT::</c> static receivers).
///
/// EXTENDING WHEN A NEW C/AL RELEASE ADDS NAMES:
///   - new record/page/report runtime method   → <see cref="ReceiverMethods"/>
///   - new no-receiver runtime function         → <see cref="BareFunctions"/>
///   - new method whose first arg is a field    → <see cref="FieldNameTakingMethods"/>
///   - new static type receiver (e.g. a module) → <see cref="StaticReceivers"/>
///   - new control/statement keyword            → <see cref="Keywords"/>
/// Adding here only silences noise; it never drops a real cross-object call,
/// because real targets resolve through the scope's typed receivers first.
/// </summary>
internal static class CalBuiltinMethods
{
    /// <summary>
    /// Runtime methods invoked on a typed receiver (<c>Rec.SETRANGE</c>,
    /// <c>Page.RUNMODAL</c>, …) or bare on the implicit Rec. Matched
    /// case-insensitively. Not exhaustive of every overload — just the names.
    /// </summary>
    public static readonly HashSet<string> ReceiverMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Record CRUD / navigation
        "GET", "FIND", "FINDFIRST", "FINDLAST", "FINDSET", "NEXT", "INSERT", "MODIFY",
        "DELETE", "DELETEALL", "MODIFYALL", "INIT", "RESET", "COPY", "RENAME",
        "ISEMPTY", "COUNT", "COUNTAPPROX",
        // Filtering / keys
        "SETRANGE", "SETFILTER", "SETCURRENTKEY", "SETVIEW", "GETVIEW", "GETFILTER",
        "GETFILTERS", "COPYFILTER", "COPYFILTERS", "MARKEDONLY", "MARK", "CLEARMARKS",
        "SETPOSITION", "GETPOSITION", "ASCENDING", "SETAUTOCALCFIELDS", "SETLOADFIELDS",
        // Fields / validation / flowfields
        "VALIDATE", "TESTFIELD", "FIELDERROR", "CALCFIELDS", "CALCSUMS", "FIELDNO",
        "FIELDNAME", "FIELDCAPTION", "FIELDACTIVE", "RECORDID", "RECORDLEVELLOCKING",
        "TABLECAPTION", "TABLENAME", "CURRENTKEY", "CURRENTKEYINDEX",
        // Locking / transactions
        "LOCKTABLE", "CONSISTENT", "CHANGECOMPANY", "SETTABLEVIEW",
        // Reflection / misc record
        "TRANSFERFIELDS", "GETRANGEMIN", "GETRANGEMAX", "GETRANGEFILTER",
        "HASFILTER", "FILTERGROUP", "NUMBER", "SYSTEMID",
        // Page / report / xmlport / query runtime
        "RUN", "RUNMODAL", "RUNREQUEST", "SETRECORD", "GETRECORD", "SETTABLEVIEW",
        "OPEN", "CLOSE", "UPDATE", "USEREQUESTPAGE", "SAVEASPDF", "SAVEASEXCEL",
        "SAVEASHTML", "SAVEASWORD", "SETDESTINATION", "SETFILENAME", "PRINT",
        "EDITABLE", "ACTIVATE", "CAPTION",
        // Codeunit
        "ISGUIALLOWED",
    };

    /// <summary>
    /// Functions with no receiver (<c>MESSAGE(...)</c>, <c>STRSUBSTNO(...)</c>).
    /// </summary>
    public static readonly HashSet<string> BareFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "MESSAGE", "ERROR", "CONFIRM", "STRMENU", "DIALOG",
        "STRSUBSTNO", "FORMAT", "EVALUATE", "STRPOS", "STRLEN", "STRCHECKSUM",
        "COPYSTR", "DELSTR", "INSSTR", "PADSTR", "CONVERTSTR", "DELCHR", "SELECTSTR",
        "LOWERCASE", "UPPERCASE", "INCSTR", "ROUND", "ABS", "POWER",
        "CLEAR", "CLEARALL", "CLEARLASTERROR", "GETLASTERRORTEXT", "GETLASTERRORCODE",
        "TODAY", "TIME", "WORKDATE", "CURRENTDATETIME", "CREATEDATETIME", "DT2DATE",
        "DT2TIME", "DMY2DATE", "CALCDATE", "DATE2DMY", "DATE2DWY", "CLOSINGDATE", "NORMALDATE",
        "MAXSTRLEN", "STRSUBSTNO", "ASSERTERROR", "ABORT", "COMMIT", "GUIALLOWED",
        "SLEEP", "EXIT", "BREAK", "QUIT", "SKIP", "SHOWOUTPUT",
        "NEWLINE", "SERIALNUMBER", "GLOBALLANGUAGE", "WINDOWSLANGUAGE", "LANGUAGE",
        "CREATEGUID", "ISNULLGUID", "FORMAT", "TYPENAME", "INCSTR", "RANDOM", "RANDOMIZE",
        "COMPRESSARRAY", "ARRAYLEN", "BINDSUBSCRIPTION", "UNBINDSUBSCRIPTION", "SESSIONID",
        "USERID", "COMPANYNAME", "SERVICEINSTANCEID", "APPLICATIONIDENTIFIER",
    };

    /// <summary>
    /// Methods whose first argument names a field on the receiver's record type
    /// — the walker emits a <c>field_access</c> for that argument.
    /// </summary>
    public static readonly HashSet<string> FieldNameTakingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "SETRANGE", "SETFILTER", "TESTFIELD", "VALIDATE", "MODIFYALL", "FIELDNO", "FIELDNAME",
        "FIELDCAPTION", "FIELDERROR", "FIELDACTIVE", "CALCFIELDS", "SETCURRENTKEY",
        "GETRANGEMIN", "GETRANGEMAX", "GETRANGEFILTER", "GETFILTER",
    };

    /// <summary>
    /// Static receivers: <c>DATABASE::Customer</c>, <c>CODEUNIT::"Sales-Post"</c>,
    /// <c>PAGE::...</c>, <c>CurrPage</c>, <c>CurrReport</c>. The walker handles
    /// the <c>::</c> forms specially; <c>Curr*</c> are skipped as runtime objects.
    /// </summary>
    public static readonly HashSet<string> StaticReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATABASE", "CODEUNIT", "PAGE", "REPORT", "XMLPORT", "QUERY", "FORM", "DATAPORT",
        "CurrPage", "CurrReport", "CurrForm", "CurrXMLport", "CurrFieldNo",
        "TABLE", "MENUSUITE", "OBJECTTYPE",
    };

    /// <summary>
    /// Statement / declaration keywords that can precede <c>(</c> or sit at a
    /// chain head but are not calls or references.
    /// </summary>
    public static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "END", "IF", "THEN", "ELSE", "CASE", "OF", "DO", "WHILE", "REPEAT",
        "UNTIL", "FOR", "TO", "DOWNTO", "WITH", "EXIT", "VAR", "AND", "OR", "NOT",
        "XOR", "DIV", "MOD", "IN", "TRUE", "FALSE", "NULL", "ARRAY",
    };

    public static bool IsReceiverMethod(string name) => ReceiverMethods.Contains(name);
    public static bool IsBareFunction(string name) => BareFunctions.Contains(name);
    public static bool IsFieldNameTaking(string name) => FieldNameTakingMethods.Contains(name);
    public static bool IsStaticReceiver(string name) => StaticReceivers.Contains(name);
    public static bool IsKeyword(string name) => Keywords.Contains(name);
}
