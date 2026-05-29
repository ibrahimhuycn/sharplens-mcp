# Razor Testing Plan — Comprehensive

> Living document. Add test cases as edge cases are discovered during implementation.

## Test Levels

| Level | What | Framework | Speed | When |
|---|---|---|---|---|
| **L0 — Spike** | Razor pipeline, mapping accuracy, symbol resolution | xUnit + FluentAssertions | <1s each | Phase 0 |
| **L1 — Unit** | Individual functions: `ProcessRazorFile`, `MapRazorToCSharp`, `TranslateLocation` | xUnit, in-memory AdhocWorkspace | ~5ms each | Phase B |
| **L2 — In-Memory Integration** | Full tool calls on hand-crafted `.razor` code | `RoslynService.LoadFromWorkspaceForTesting` + in-memory docs | ~50ms each | Phases C-F |
| **L3 — Solution Integration** | Real Blazor project loaded via MSBuildWorkspace | `RoslynServiceTestBase` | ~30s amortized | Phase G |
| **L4 — Refactoring Apply** | Preview → apply → verify `.razor` file on disk | xUnit, temp directories | ~200ms each | Phase D |

---

## Test Fixture Catalog

### In-Memory Fixtures (L1/L2)

Each fixture is a `(string razorSource, string fileName)` tuple. Virtual `.cs` code-behind files can be added as separate documents in the same project.

```csharp
// Fixture definitions used across tests
public static class RazorFixtures
{
    // ---- Basic ----
    public const string Counter = @"
@code {
    private int _counter;

    private void Increment()
    {
        _counter++;
    }
}";

    public const string Property = @"
@code {
    public string Title { get; set; }
    
    private void UseTitle()
    {
        var x = Title;
    }
}";

    public const string Method = @"
@code {
    private int Add(int a, int b)
    {
        return a + b;
    }

    private void Caller()
    {
        var result = Add(1, 2);
    }
}";

    // ---- Component parameters ----
    public const string Parameters = @"
@code {
    [Parameter] public string Label { get; set; }
    [Parameter] public int Count { get; set; }
    [Parameter] public EventCallback<int> OnChanged { get; set; }
    
    private async Task HandleClick()
    {
        await OnChanged.InvokeAsync(Count);
    }
}";

    // ---- Event handling ----
    public const string EventHandling = @"
<button @onclick=""HandleClick"">Click me</button>

@code {
    private int _clicks;
    
    private void HandleClick()
    {
        _clicks++;
    }
}";

    public const string EventHandlingWithArgs = @"
<button @onclick=""e => HandleClick(e, 42)"">Click</button>

@code {
    private void HandleClick(MouseEventArgs e, int value)
    {
    }
}";

    // ---- Data binding ----
    public const string DataBinding = @"
<InputText @bind-Value=""Name"" />
<InputText @bind-Value=""Name"" @bind-Value:event=""oninput"" />
<InputNumber @bind-Value=""_age"" />

@code {
    private string Name { get; set; }
    private int _age;
}";

    // ---- Control flow ----
    public const string ConditionalRendering = @"
@if (_isVisible)
{
    <p>The count is @_count</p>
}
else
{
    <span>@Message</span>
}

@code {
    private bool _isVisible = true;
    private int _count;
    private string Message => _count > 0 ? ""active"" : ""empty"";
}";

    public const string LoopRendering = @"
@foreach (var item in _items)
{
    <li>@item.Name</li>
}

@code {
    private List<Item> _items = new();
    
    public class Item
    {
        public string Name { get; set; }
    }
}";

    public const string ForLoop = @"
@for (int i = 0; i < _max; i++)
{
    <span>@i</span>
}

@code {
    private int _max = 10;
}";

    // ---- Generics ----
    public const string GenericComponent = @"
@typeparam TItem

@foreach (var item in Items)
{
    <div>@ChildContent(item)</div>
}

@code {
    [Parameter] public List<TItem> Items { get; set; }
    [Parameter] public RenderFragment<TItem> ChildContent { get; set; }
}";

    // ---- Inheritance ----
    public const string InheritsBase = @"
@inherits MyBaseComponent

@code {
    public override string GetTitle() => ""Override"";
}";
    
    // Separate C# fixture for the base class (added as .cs document)
    public const string MyBaseComponent = @"
using Microsoft.AspNetCore.Components;
public abstract class MyBaseComponent : ComponentBase
{
    public abstract string GetTitle();
}";

    // ---- Implements interface ----
    public const string ImplementsInterface = @"
@implements IDisposable

@code {
    private bool _disposed;
    
    public void Dispose()
    {
        _disposed = true;
    }
}";

    // ---- Dependency injection ----
    public const string InjectService = @"
@inject IJSRuntime JS
@inject NavigationManager Nav

@code {
    private async Task Navigate()
    {
        await JS.InvokeVoidAsync(""go"");
        Nav.NavigateTo(""/home"");
    }
}";

    // ---- Attribute ----
    public const string AttributeUsage = @"
@attribute [Authorize]

@code {
    public void DoSomething() { }
}";

    // ---- Code-behind (partial class) ----
    public const string CodeBehind_Razor = @"
@code {
    // Implementation in .razor.cs
    public partial class CounterWithCodeBehind
    {
        private string _message;
        
        public string DisplayMessage => _message?.ToUpper();
    }
}";
    
    public const string CodeBehind_CS = @"
using Microsoft.AspNetCore.Components;

namespace TestApp;
public partial class CounterWithCodeBehind : ComponentBase
{
    private int _backendField;
    
    public async Task InitializeAsync()
    {
        _message = _backendField.ToString();
    }
}";

    // ---- Multiple code blocks ----
    public const string MultipleCodeBlocks = @"
@code {
    private int _first;
}

<div>@_first</div>

@code {
    private int _second;
}

<span>@_second</span>";

    // ---- Inline expressions ----
    public const string InlineExpressions = @"
<h1>@DateTime.Now.ToShortDateString()</h1>
<p>@(Math.Max(5, _value))</p>
<p>Total: @(_items.Count)</p>

@code {
    private int _value = 3;
    private List<string> _items = new();
}";

    // ---- Keyed elements ----
    public const string KeyedElements = @"
@foreach (var person in _people)
{
    <div @key=""person.Id"">
        @person.Name
    </div>
}

@code {
    private List<Person> _people = new();
    public class Person { public int Id { get; set; } public string Name { get; set; } }
}";

    // ---- Child content / RenderFragment ----
    public const string RenderFragmentChild = @"
@ChildContent

@code {
    [Parameter] public RenderFragment ChildContent { get; set; }
}";

    // ---- Cascading parameter ----
    public const string CascadingParameter = @"
@code {
    [CascadingParameter] public string Theme { get; set; }
    
    private string GetStyle() => Theme == ""dark"" ? ""bg-black"" : ""bg-white"";
}";

    // ---- Pure markup (no C#) ----
    public const string PureMarkup = @"
<h1>Hello, World!</h1>
<p>This component has no code.</p>";

    // ---- Empty file ----
    public const string EmptyFile = @"";

    // ---- Whitespace only ----
    public const string WhitespaceOnly = @"

   ";

    // ---- Component reference ----
    public const string ComponentRef = @"
<SurveyPrompt Title=""How are you?"" />

@code {
    private string _feedback;
}";

    // ---- Ref element capture ----
    public const string ElementRef = @"
<div @ref=""_myDiv"">Content</div>
<button @ref=""_myButton"" @onclick=""FocusDiv"">Focus</button>

@code {
    private ElementReference _myDiv;
    private ElementReference _myButton;
    
    private async Task FocusDiv()
    {
        await _myDiv.FocusAsync();
    }
}";

    // ---- Lifecycle methods ----
    public const string Lifecycle = @"
@code {
    private string _state;
    
    protected override async Task OnInitializedAsync()
    {
        _state = await LoadDataAsync();
    }
    
    protected override void OnParametersSet()
    {
        if (_state == null)
            _state = ""default"";
    }
    
    private async Task<string> LoadDataAsync() => ""loaded"";
}";

    // ---- Nested lambdas ----
    public const string NestedLambdas = @"
@code {
    private Func<int, Func<int, int>> _curried = a => b => a + b;
    
    private void Execute()
    {
        var result = _curried(1)(2);
    }
}";

    // ---- Complex expressions in markup ----
    public const string ComplexMarkupExpressions = @"
<div class=""@(_isActive ? ""active"" : ""inactive"") @(_isLarge ? ""large"" : """")"">
    @if (_items?.Any() == true)
    {
        foreach (var item in _items.Where(i => i.IsVisible))
        {
            <span>@item.Name</span>
        }
    }
</div>

@code {
    private bool _isActive = true;
    private bool _isLarge;
    private List<Item>? _items;
    public class Item { public string Name { get; set; } public bool IsVisible { get; set; } }
}";

    // ---- Extension method usage ----
    public const string ExtensionMethod = @"
@code {
    private string _list => GetFormattedList();
    
    private string GetFormattedList()
    {
        return new[] { 1, 2, 3 }
            .Select(x => x.ToString())
            .Aggregate((a, b) => a + "", "" + b);
    }
}";

    // ---- Local functions ----
    public const string LocalFunction = @"
@code {
    private int Process(int input)
    {
        return AddTwo(input);
        
        static int AddTwo(int x) => x + 2;
    }
}";

    // ---- Async method missing CancellationToken ----
    public const string MissingCancellationToken = @"
@code {
    private async Task LoadData()
    {
        await Task.Delay(1000);
    }
}";

    // ---- Overloaded methods ----
    public const string OverloadedMethods = @"
@code {
    private string Format(int value) => value.ToString();
    private string Format(double value) => value.ToString(""F2"");
    private string Format(DateTime value) => value.ToShortDateString();
    
    private void UseOverloads()
    {
        var a = Format(5);
        var b = Format(3.14);
        var c = Format(DateTime.Now);
    }
}";

    // ---- Region / pragma (edge: Roslyn handling in generated C#) ----
    public const string RegionInCodeBlock = @"
@code {
    private void Method()
    {
        #region Setup
        var x = 1;
        #endregion
        
        #pragma warning disable CS0219
        var unused = 2;
        #pragma warning restore CS0219
    }
}";

    // ---- Long file (500+ lines of C# in code block) ----
    // Generated programmatically in test setup
    public static string LongCodeBlock(int methodCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@code {");
        for (int i = 0; i < methodCount; i++)
            sb.AppendLine($"    private int Method{i}() => {i};");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ---- Unicode identifiers (C# allows) ----
    public const string UnicodeIdentifiers = @"
@code {
    private int _日本語フィールド;
    private void Überprüfen() 
    {
        _日本語フィールド = 42;
    }
}";

    // ---- Enum usage ----
    public const string EnumInCodeBlock = @"
@code {
    private Status _currentStatus = Status.Active;
    
    private void ChangeStatus()
    {
        _currentStatus = Status.Inactive;
    }
    
    public enum Status { Active, Inactive, Pending }
}";

    // ---- Record type in code block ----
    public const string RecordInCodeBlock = @"
@code {
    private UserRecord? _currentUser;
    
    private void SetUser(UserRecord user)
    {
        _currentUser = user with { Name = ""Updated"" };
    }
    
    public record UserRecord(string Name, int Age);
}";
}
```

---

## Edge Case Matrix

Each construct × each tool category. ✓ = must test. — = not applicable.

| Construct | get_symbol_info | go_to_definition | find_references | find_callers | rename_symbol | extract_method | get_diagnostics | get_complexity |
|---|---|---|---|---|---|---|---|---|
| `@code { int x; }` | ✓ | ✓ | ✓ | — | ✓ | — | ✓ | ✓ |
| `@code { void M() }` | ✓ | ✓ | ✓ | ✓ | ✓ | — | ✓ | ✓ |
| `@code { void M() { M2(); } }` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `[Parameter]` property | ✓ | ✓ | ✓ | ✓ | ✓ | — | ✓ | — |
| `@bind-Value="Name"` | ✓ | ✓ | ✓ | ✓ | ✓ | — | — | — |
| `@bind-Value:event="oninput"` | ✓ | ✓ | ✓ | — | ✓ | — | — | — |
| `@onclick="Handler"` | ✓ | ✓ | ✓ | — | ✓ | — | — | — |
| `@onclick="e => Handler(e)"` | ✓ | ✓ | ✓ | — | ✓ | — | — | — |
| `@onclick="() => _count++"` | — | — | ✓ | — | — | — | — | — |
| `@ref="_element"` | ✓ | ✓ | ✓ | — | ✓ | — | — | — |
| `@typeparam TItem` | ✓ | ✓ | ✓ | — | ✓ | — | — | — |
| `@if (cond) { ... }` | ✓ | — | ✓ | — | ✓ | — | ✓ | — |
| `@foreach (var x in items)` | ✓ | — | ✓ | — | ✓ | — | ✓ | — |
| `@for (int i = 0; ...)` | ✓ | — | ✓ | — | ✓ | — | ✓ | — |
| `@while` | ✓ | — | ✓ | — | ✓ | — | ✓ | — |
| `@(expr)` inline | ✓ | — | ✓ | — | — | — | — | — |
| `@DateTime.Now` inline | ✓ | ✓ | ✓ | — | — | — | — | — |
| `@key="item.Id"` | ✓ | — | ✓ | — | — | — | — | — |
| `@inject IService svc` | ✓ | ✓ | ✓ | — | — | — | — | — |
| `@implements IDisposable` | ✓ | — | — | — | — | — | ✓ | — |
| `@inherits BaseClass` | ✓ | — | — | — | — | — | ✓ | — |
| `@attribute [Auth]` | ✓ | — | — | — | — | — | — | — |
| `@page "/route"` | — | — | — | — | — | — | — | — |
| `@layout MainLayout` | — | — | — | — | — | — | — | — |
| No `@code` block | — | — | — | — | — | — | ✓ | — |
| Pure markup only | — | — | — | — | — | — | ✓ | — |
| Local function in @code | ✓ | ✓ | ✓ | — | ✓ | — | ✓ | ✓ |
| Nested class in @code | ✓ | ✓ | ✓ | — | — | — | ✓ | — |
| Record in @code | ✓ | ✓ | ✓ | — | — | — | ✓ | — |
| Enum in @code | ✓ | ✓ | ✓ | — | — | — | ✓ | — |
| Extension methods | ✓ | ✓ | ✓ | ✓ | — | — | ✓ | — |
| Async without CT | ✓ | — | — | — | — | — | ✓ | — |
| Regional / #pragma | ✓ | — | — | — | — | — | ✓ | — |
| Unicode identifiers | ✓ | ✓ | ✓ | — | ✓ | — | ✓ | — |
| Long file (500+ lines) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Multiple @code blocks | ✓ | ✓ | ✓ | — | ✓ | — | ✓ | ✓ |
| Code-behind (.razor + .cs) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Empty file | — | — | — | — | — | — | ✓ | — |
| Whitespace only | — | — | — | — | — | — | ✓ | — |

---

## L1 — Unit Tests

### L1-MAP: Position Mapping Tests

```csharp
public class RazorMappingTests
{
    // ---- Forward mapping (razor → C#) ----
    
    [Fact]
    public void MapRazorToCSharp_FieldDeclaration_ReturnsCorrectPosition() { }
    
    [Fact]
    public void MapRazorToCSharp_MethodDeclaration_ReturnsCorrectPosition() { }
    
    [Fact]
    public void MapRazorToCSharp_PropertyDeclaration_ReturnsCorrectPosition() { }
    
    [Fact]
    public void MapRazorToCSharp_FieldReference_InMethodBody() { }
    
    [Fact]
    public void MapRazorToCSharp_MethodCall_InMethodBody() { }
    
    [Fact]
    public void MapRazorToCSharp_InlineExpression_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_ParameterProperty_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_EventCallbackProperty_MapsCorrectly() { }
    
    // ---- Forward mapping: control flow constructs ----
    
    [Fact]
    public void MapRazorToCSharp_IfCondition_MapsToGeneratedIf() { }
    
    [Fact]
    public void MapRazorToCSharp_ForeachVariable_MapsToGeneratedForeach() { }
    
    [Fact]
    public void MapRazorToCSharp_ForLoopVariable_MapsToGeneratedFor() { }
    
    [Fact]
    public void MapRazorToCSharp_WhileCondition_MapsToGeneratedWhile() { }
    
    [Fact]
    public void MapRazorToCSharp_SwitchExpression_MapsToGeneratedSwitch() { }
    
    // ---- Forward mapping: binding/event constructs ----
    
    [Fact]
    public void MapRazorToCSharp_BindValue_MapsToPropertyReference() { }
    
    [Fact]
    public void MapRazorToCSharp_BindValueEvent_MapsToPropertyReference() { }
    
    [Fact]
    public void MapRazorToCSharp_OnclickMethodRef_MapsToMethodReference() { }
    
    [Fact]
    public void MapRazorToCSharp_OnclickLambda_MapsMethodInsideLambda() { }
    
    [Fact]
    public void MapRazorToCSharp_OnclickArrowExpression_MapsExpression() { }
    
    [Fact]
    public void MapRazorToCSharp_ElementRef_MapsToFieldReference() { }
    
    [Fact]
    public void MapRazorToCSharp_Key_MapsToPropertyAccess() { }
    
    // ---- Forward mapping: type-level constructs ----
    
    [Fact]
    public void MapRazorToCSharp_TypeParam_MapsToGenericParameter() { }
    
    [Fact]
    public void MapRazorToCSharp_Injects_MapsToInjectedField() { }
    
    [Fact]
    public void MapRazorToCSharp_Implements_MapsToInterfaceDeclaration() { }
    
    [Fact]
    public void MapRazorToCSharp_Inherits_MapsToBaseClassDeclaration() { }
    
    [Fact]
    public void MapRazorToCSharp_Attribute_MapsToClassAttribute() { }
    
    // ---- Forward mapping: edge positions ----
    
    [Fact]
    public void MapRazorToCSharp_FirstCharOfCodeBlock_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_LastCharOfCodeBlock_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_LineBreakInCodeBlock_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_PositionAtCodeBlockBoundary_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_MultipleCodeBlocks_EachMapsIndependently() { }
    
    [Fact]
    public void MapRazorToCSharp_NestedClassInCodeBlock_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_RecordTypeInCodeBlock_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_EnumInCodeBlock_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_LocalFunction_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_UnicodeIdentifier_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_RegionAndPragma_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_ExpressionBodiedMember_MapsCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_PatternMatchingExpression_MapsCorrectly() { }
    
    // ---- Forward mapping: markup-adjacent positions ----
    
    [Fact]
    public void MapRazorToCSharp_PositionInMarkup_ReturnsNull() { }
    
    [Fact]
    public void MapRazorToCSharp_PositionInHtmlTag_ReturnsNull() { }
    
    [Fact]
    public void MapRazorToCSharp_PositionInTextContent_ReturnsNull() { }
    
    [Fact]
    public void MapRazorToCSharp_PositionInComment_ReturnsNull() { }
    
    [Fact]
    public void MapRazorToCSharp_PositionInEmptyFile_ReturnsNull() { }
    
    [Fact]
    public void MapRazorToCSharp_PositionInWhitespaceOnly_ReturnsNull() { }
    
    [Fact]
    public void MapRazorToCSharp_RazorComment_HandledCorrectly() { }
    
    [Fact]
    public void MapRazorToCSharp_CSharpExpressionInsideMarkup_MapsOnlyExpression() { }
    
    // ---- Reverse mapping (C# → razor) ----
    
    [Fact]
    public void MapCSharpToRazor_FieldInGeneratedCode_MapsBackToRazor() { }
    
    [Fact]
    public void MapCSharpToRazor_MethodInGeneratedCode_MapsBackToRazor() { }
    
    [Fact]
    public void MapCSharpToRazor_GeneratedOnlyCode_BuildRenderTree_ReturnsNull() { }
    
    [Fact]
    public void MapCSharpToRazor_GeneratedOnlyCode_TypeInference_ReturnsNull() { }
    
    [Fact]
    public void MapCSharpToRazor_GeneratedOnlyCode_ComponentCapture_ReturnsNull() { }
    
    [Fact]
    public void MapCSharpToRazor_RenderTreeBuilder_ReturnsNull() { }
    
    [Fact]
    public void MapCSharpToRazor_AttributeBuilder_ReturnsNull() { }
    
    // ---- Round-trip tests ----
    
    [Theory]
    [InlineData("private int _x;", "_x", 0)]  // field
    [InlineData("private void M() {}", "M", 0)] // method
    [InlineData("public string P { get; set; }", "P", 0)] // property
    [InlineData("var x = _field;", "_field", 0)] // reference
    [InlineData("CallMethod();", "CallMethod", 0)] // call
    [InlineData("list.Where(x => x > 0)", "Where", 0)] // extension method
    [InlineData("list.Where(x => x > 0)", "x", 0)] // lambda param
    [InlineData("Func<int,int> f = a => a+1;", "a", 0)] // lambda param 2
    public void RoundTrip_RazorToCSharpToRazor_ReturnsOriginalPosition(
        string codeInCodeBlock, string identifier, int toleranceChars) { }
    
    // ---- TranslateLocation tests ----
    
    [Fact]
    public void TranslateLocation_VirtualDocument_ReturnsRazorPath() { }
    
    [Fact]
    public void TranslateLocation_RealDocument_ReturnsAsIs() { }
    
    [Fact]
    public void TranslateLocation_NullSourceTree_ReturnsEmpty() { }
    
    [Fact]
    public void TranslateLocation_NoDocumentForSourceTree_ReturnsOriginalPath() { }
    
    [Fact]
    public void TranslateLocation_LocationSpanningMultipleLines_PreservesEndPosition() { }
    
    // ---- Multiple documents mapping ----
    
    [Fact]
    public void MapRazorToCSharp_MultipleRazorFiles_EachMapsCorrectly() { }
    
    [Fact]
    public void TranslateLocation_CrossFile_ReturnsCorrectRazorFileForEach() { }
    
    // ---- Performance ----
    
    [Fact]
    public void MapRazorToCSharp_LargeFile_ReturnsWithinThreshold() { }
    
    [Fact]
    public void ProcessRazorFile_FiftyFiles_CompletesUnderThreeSeconds() { }
}
```

### L1-DOC: Document Resolution Tests

```csharp
public class RazorDocumentResolutionTests
{
    // ---- TryFindDocument routing ----
    
    [Fact]
    public void TryFindDocument_RazorFilePath_ReturnsVirtualDocument() { }
    
    [Fact]
    public void TryFindDocument_RazorRelativePath_ReturnsVirtualDocument() { }
    
    [Fact]
    public void TryFindDocument_RazorAbsolutePath_ReturnsVirtualDocument() { }
    
    [Fact]
    public void TryFindDocument_NormalCsFile_ReturnsCsDocument() { }
    
    [Fact]
    public void TryFindDocument_UnknownRazorFile_ReturnsNull() { }
    
    [Fact]
    public void TryFindDocument_RazorFileBeforeProcessing_ReturnsVirtualDoc() { }
    
    // ---- GetRazorFileInfo lazy processing ----
    
    [Fact]
    public void GetRazorFileInfo_FirstAccess_ProcessesAndCaches() { }
    
    [Fact]
    public void GetRazorFileInfo_SecondAccess_ReturnsCachedWithoutReprocessing() { }
    
    [Fact]
    public void GetRazorFileInfo_NonexistentFile_ReturnsNull() { }
    
    [Fact]
    public void GetRazorFileInfo_CodeBehindPartialClass_IncludesBothFiles() { }
    
    // ---- IsRazorProject detection ----
    
    [Fact]
    public void IsRazorProject_WithComponentsReference_ReturnsTrue() { }
    
    [Fact]
    public void IsRazorProject_WithoutComponentsReference_ReturnsFalse() { }
    
    [Fact]
    public void IsRazorProject_ClassLibraryWithComponents_ReturnsTrue() { }
    
    // ---- IsRazorGeneratedDocument detection ----
    
    [Fact]
    public void IsRazorGeneratedDocument_VirtualDoc_ReturnsTrue() { }
    
    [Fact]
    public void IsRazorGeneratedDocument_RealCsDoc_ReturnsFalse() { }
    
    // ---- Cache behavior ----
    
    [Fact]
    public void InvalidateRazorFile_RemovesFromRegistryAndSolution() { }
    
    [Fact]
    public void CacheClear_AfterSyncDocuments_RemovesRazorEntries() { }
    
    [Fact]
    public void CacheClear_AfterLoadSolution_RemovesRazorEntries() { }
    
    // ---- Synthetic path generation ----
    
    [Fact]
    public void AddVirtualRazorDocument_FilePath_IsUnique() { }
    
    [Fact]
    public void AddVirtualRazorDocument_FilePath_DoesNotCollideWithRealFiles() { }
    
    [Fact]
    public void AddVirtualRazorDocument_Folders_PreserveDirectoryStructure() { }
    
    // ---- Generated C# compilation ----
    
    [Fact]
    public async Task VirtualDocument_Compiles_WithProjectReferences() { }
    
    [Fact]
    public async Task VirtualDocument_CodeBehind_CompilesAsPartial() { }
    
    [Fact]
    public async Task VirtualDocument_ReferencesCodeBehindFields() { }
    
    [Fact]
    public async Task VirtualDocument_MultipleRazorFiles_AllCompile() { }
}
```

---

## L2 — In-Memory Integration Tests

### L2-NAV: Navigation Tools on Razor

```csharp
public class RazorNavigationTests
{
    // ---- get_symbol_info ----
    
    [Fact] public async Task GetSymbolInfo_OnRazorField_ReturnsFieldSymbol() { }
    [Fact] public async Task GetSymbolInfo_OnRazorMethod_ReturnsMethodSymbol() { }
    [Fact] public async Task GetSymbolInfo_OnRazorProperty_ReturnsPropertySymbol() { }
    [Fact] public async Task GetSymbolInfo_OnRazorParameter_ReturnsParameterAttribute() { }
    [Fact] public async Task GetSymbolInfo_OnBindValue_ReturnsPropertySymbol() { }
    [Fact] public async Task GetSymbolInfo_OnOnclick_ReturnsMethodSymbol() { }
    [Fact] public async Task GetSymbolInfo_OnInlineExpression_ReturnsSymbol() { }
    [Fact] public async Task GetSymbolInfo_OnPositionInMarkup_ReturnsError() { }
    [Fact] public async Task GetSymbolInfo_OnWhitespace_ReturnsError() { }
    
    // ---- go_to_definition ----
    
    [Fact] public async Task GoToDefinition_OnFieldUsage_JumpsToDeclaration() { }
    [Fact] public async Task GoToDefinition_OnMethodCall_JumpsToDeclaration() { }
    [Fact] public async Task GoToDefinition_OnPropertyAccess_JumpsToDeclaration() { }
    [Fact] public async Task GoToDefinition_OnParameterProperty_JumpsToDeclaration() { }
    [Fact] public async Task GoToDefinition_OnBindValue_JumpsToProperty() { }
    [Fact] public async Task GoToDefinition_OnOnclick_JumpsToMethod() { }
    [Fact] public async Task GoToDefinition_OnTypeParam_JumpsToTypeParamDeclaration() { }
    [Fact] public async Task GoToDefinition_CrossFile_RazorToCodeBehind() { }
    [Fact] public async Task GoToDefinition_CrossFile_CodeBehindToRazor() { }
    [Fact] public async Task GoToDefinition_ReturnsRazorFilePathAndPosition_NotGeneratedFile() { }
    
    // ---- find_references ----
    
    [Fact] public async Task FindReferences_OnRazorField_FindsAllUsages() { }
    [Fact] public async Task FindReferences_OnRazorMethod_FindsAllCalls() { }
    [Fact] public async Task FindReferences_OnRazorProperty_FindsAllAccesses() { }
    [Fact] public async Task FindReferences_OnParameterProperty_FindsBindUsage() { }
    [Fact] public async Task FindReferences_CrossFile_RazorAndCodeBehind() { }
    [Fact] public async Task FindReferences_BindValue_FindsPropertyDeclaration() { }
    [Fact] public async Task FindReferences_Onclick_FindsMethodDeclaration() { }
    [Fact] public async Task FindReferences_OnclickLambda_FindsCapturedVariables() { }
    [Fact] public async Task FindReferences_ForLoopVariable_FindsAllUsages() { }
    [Fact] public async Task FindReferences_ForeachVariable_FindsAllUsages() { }
    [Fact] public async Task FindReferences_AllReferencesHaveRazorPath_NotGeneratedPath() { }
    [Fact] public async Task FindReferences_WithKindFilter_ReturnsCorrectKinds() { }
    
    // ---- find_implementations ----
    
    [Fact] public async Task FindImplementations_OnRazorInterface_FindsRazorImplementor() { }
    [Fact] public async Task FindImplementations_OnAbstractBase_FindsRazorOverride() { }
    [Fact] public async Task FindImplementations_OnIDisposable_FindsRazorDisposeMethod() { }
    
    // ---- get_type_hierarchy ----
    
    [Fact] public async Task GetTypeHierarchy_OnRazorComponent_ShowsComponentBase() { }
    [Fact] public async Task GetTypeHierarchy_OnInheritedComponent_ShowsFullChain() { }
    [Fact] public async Task GetTypeHierarchy_OnRazorRecord_ShowsRecordBase() { }
    
    // ---- get_method_overloads ----
    
    [Fact] public async Task GetMethodOverloads_OnOverloadedMethod_ReturnsAll() { }
    [Fact] public async Task GetMethodOverloads_OnNonOverloaded_ReturnsSingleMethod() { }
    
    // ---- get_containing_member ----
    
    [Fact] public async Task GetContainingMember_OnFieldReference_ShowsContainingMethod() { }
    [Fact] public async Task GetContainingMember_OnTopLevelField_ShowsNoContainingMember() { }
    
    // ---- find_callers ----
    
    [Fact] public async Task FindCallers_OnRazorMethod_FindsCallersInRazor() { }
    [Fact] public async Task FindCallers_OnRazorMethod_FindsCallersInCodeBehind() { }
    [Fact] public async Task FindCallers_OnLifecycleMethod_FindsImplicitCallers() { }
    
    // ---- get_outgoing_calls ----
    
    [Fact] public async Task GetOutgoingCalls_OnRazorMethod_FindsCalleesInRazor() { }
    [Fact] public async Task GetOutgoingCalls_OnRazorMethod_FindsCalleesInOtherRazorFiles() { }
}
```

### L2-ANALYSIS: Analysis Tools on Razor

```csharp
public class RazorAnalysisTests
{
    // ---- get_diagnostics ----
    
    [Fact] public async Task GetDiagnostics_ValidRazor_NoErrors() { }
    [Fact] public async Task GetDiagnostics_InvalidCSharp_ShowsErrorsAtRazorPosition() { }
    [Fact] public async Task GetDiagnostics_UndefinedVariable_ShowsError() { }
    [Fact] public async Task GetDiagnostics_RazorParseError_ShowsRazorDiagnostic() { }
    [Fact] public async Task GetDiagnostics_ErrorPosition_IsRazorPathNotGeneratedPath() { }
    [Fact] public async Task GetDiagnostics_ByRazorFilePath_FiltersToThatFile() { }
    [Fact] public async Task GetDiagnostics_ByProjectPath_IncludesRazorDiagnostics() { }
    [Fact] public async Task GetDiagnostics_WithSeverityFilter_IncludesRazorDiagnostics() { }
    [Fact] public async Task GetDiagnostics_RunAnalyzersFalse_CompilerErrorsOnly() { }
    [Fact] public async Task GetDiagnostics_MultipleRazorFiles_EachReportsOwnErrors() { }
    
    // ---- analyze_data_flow ----
    
    [Fact] public async Task AnalyzeDataFlow_InRazorBlock_TracksVariableUsage() { }
    [Fact] public async Task AnalyzeDataFlow_AcrossCodeBlocks_TracksAllBlocks() { }
    [Fact] public async Task AnalyzeDataFlow_ResultLocations_AreRazorPaths() { }
    [Fact] public async Task AnalyzeDataFlow_PositionInMarkup_ReturnsError() { }
    
    // ---- analyze_control_flow ----
    
    [Fact] public async Task AnalyzeControlFlow_IfElseInCodeBlock_ReturnsBranches() { }
    [Fact] public async Task AnalyzeControlFlow_ForeachLoop_ReturnsLoopRegions() { }
    [Fact] public async Task AnalyzeControlFlow_SwitchStatement_ReturnsCaseRegions() { }
    
    // ---- get_complexity_metrics ----
    
    [Fact] public async Task GetComplexityMetrics_SimpleMethod_ReturnsLowComplexity() { }
    [Fact] public async Task GetComplexityMetrics_NestedIfs_ReturnsHigherComplexity() { }
    [Fact] public async Task GetComplexityMetrics_FilePath_IsRazorFile() { }
    [Fact] public async Task GetComplexityMetrics_MeasuresMethodsInCodeBlock() { }
    
    // ---- get_call_graph ----
    
    [Fact] public async Task GetCallGraph_FromRazorMethod_TracesCalleeChain() { }
    [Fact] public async Task GetCallGraph_CrossRazorFiles_IncludesOtherFiles() { }
    [Fact] public async Task GetCallGraph_NodePaths_AreRazorPaths() { }
    [Fact] public async Task GetCallGraph_CycleDetection_HandlesRazorCallers() { }
    
    // ---- analyze_change_impact ----
    
    [Fact] public async Task AnalyzeChangeImpact_OnRazorProperty_FindsAllAffected() { }
    [Fact] public async Task AnalyzeChangeImpact_OnRazorMethod_FindsCallers() { }
}
```

### L2-TYPE: Type Discovery Tools on Razor

```csharp
public class RazorTypeDiscoveryTests
{
    // ---- search_symbols ----
    
    [Fact] public async Task SearchSymbols_Pattern_FindsRazorComponentType() { }
    [Fact] public async Task SearchSymbols_Name_FindsRazorComponentType() { }
    [Fact] public async Task SearchSymbols_ResultPath_IsRazorFileNotGeneratedFile() { }
    [Fact] public async Task SearchSymbols_DoesNotReturnVirtualDocumentTypes() { }
    
    // ---- semantic_query ----
    
    [Fact] public async Task SemanticQuery_IsAsync_FindsAsyncRazorMethods() { }
    [Fact] public async Task SemanticQuery_Kinds_FindsRazorFields() { }
    [Fact] public async Task SemanticQuery_FindsTypesWithCancellationTokenGap() { }
    
    // ---- get_type_members ----
    
    [Fact] public async Task GetTypeMembers_RazorComponentType_ReturnsMembers() { }
    [Fact] public async Task GetTypeMembers_IncludesRazorParameters() { }
    [Fact] public async Task GetTypeMembers_IncludesRazorMethods() { }
    [Fact] public async Task GetTypeMembers_IncludesRazorFields() { }
    [Fact] public async Task GetTypeMembers_IncludesInheritedMembers() { }
    
    // ---- get_method_signature ----
    
    [Fact] public async Task GetMethodSignature_OnRazorComponent_FindsMethod() { }
    [Fact] public async Task GetMethodSignature_ReturnsRazorLocation() { }
    
    // ---- get_derived_types ----
    
    [Fact] public async Task GetDerivedTypes_OnComponentBase_IncludesRazorComponents() { }
    [Fact] public async Task GetDerivedTypes_OnBaseClass_IncludesInheritingRazor() { }
    
    // ---- get_base_types ----
    
    [Fact] public async Task GetBaseTypes_OnRazorComponent_ShowsComponentBase() { }
    [Fact] public async Task GetBaseTypes_OnInheritedRazor_ShowsFullChain() { }
    
    // ---- get_attributes ----
    
    [Fact] public async Task GetAttributes_OnRazorWithAttribute_ReturnsAttribute() { }
    [Fact] public async Task GetAttributes_OnParameterProperty_ReturnsParameterAttribute() { }
    
    // ---- get_type_members_batch ----
    
    [Fact] public async Task GetTypeMembersBatch_MixedCsAndRazorTypes_ReturnsAll() { }
    
    // ---- check_type_compatibility ----
    
    [Fact] public async Task CheckTypeCompatibility_RazorComponentToComponentBase_Compatible() { }
    
    // ---- get_instantiation_options ----
    
    [Fact] public async Task GetInstantiationOptions_RazorComponentType_ShowsConstructors() { }
}
```

### L2-REF: Refactoring Tools on Razor

```csharp
public class RazorRefactoringTests
{
    // ---- rename_symbol ----
    
    [Fact] public async Task RenameSymbol_FieldInCodeBlock_RenamesAllOccurrences() { }
    [Fact] public async Task RenameSymbol_MethodInCodeBlock_RenamesAllCalls() { }
    [Fact] public async Task RenameSymbol_ParameterProperty_RenamesAllBindings() { }
    [Fact] public async Task RenameSymbol_CrossFile_RazorAndCodeBehind() { }
    [Fact] public async Task RenameSymbol_CrossFile_RazorToRazor() { }
    [Fact] public async Task RenameSymbol_OnclickHandler_RenamesHandlerAndBinding() { }
    [Fact] public async Task RenameSymbol_BindValueProperty_RenamesPropertyAndBinding() { }
    [Fact] public async Task RenameSymbol_Preview_ShowsRazorDiffNotGeneratedDiff() { }
    [Fact] public async Task RenameSymbol_Preview_AllPathsAreRazorFiles() { }
    [Fact] public async Task RenameSymbol_Apply_ModifiesRazorOnDisk() { }
    [Fact] public async Task RenameSymbol_Apply_PreservesMarkupIntact() { }
    [Fact] public async Task RenameSymbol_Apply_RegeneratedCSharpCompiles() { }
    [Fact] public async Task RenameSymbol_InvalidIdentifier_ReturnsError() { }
    [Fact] public async Task RenameSymbol_ConflictsWithExistingSymbol_ReturnsError() { }
    
    // ---- encapsulate_field ----
    
    [Fact] public async Task EncapsulateField_SimpleField_GeneratesProperty() { }
    [Fact] public async Task EncapsulateField_PropertyInsertedInCodeBlock() { }
    [Fact] public async Task EncapsulateField_ReferencesUpdated() { }
    [Fact] public async Task EncapsulateField_Preview_ShowsChangeInRazor() { }
    [Fact] public async Task EncapsulateField_Apply_FileContainsNewProperty() { }
    
    // ---- inline_variable ----
    
    [Fact] public async Task InlineVariable_SimpleLocal_InlinesUsage() { }
    [Fact] public async Task InlineVariable_SingleUsage_RemovesDeclaration() { }
    [Fact] public async Task InlineVariable_MultipleUsage_InlinesAll() { }
    
    // ---- extract_variable ----
    
    [Fact] public async Task ExtractVariable_SimpleExpression_ExtractsCorrectly() { }
    [Fact] public async Task ExtractVariable_ComplexExpression_ExtractsWithType() { }
    
    // ---- extract_method ----
    
    [Fact] public async Task ExtractMethod_SimpleBlock_ExtractsToMethod() { }
    [Fact] public async Task ExtractMethod_WithParameters_ExtractsWithCorrectSignature() { }
    [Fact] public async Task ExtractMethod_CallSiteReplaced() { }
    [Fact] public async Task ExtractMethod_NewMethodInCodeBlock() { }
    [Fact] public async Task ExtractMethod_Preview_ShowsNewMethodAndCallSite() { }
    [Fact] public async Task ExtractMethod_Apply_FileContainsNewMethod() { }
    [Fact] public async Task ExtractMethod_Apply_OriginalLogicStillWorks() { }
    
    // ---- change_signature ----
    
    [Fact] public async Task ChangeSignature_AddParameter_UpdatesDeclarationAndCallers() { }
    [Fact] public async Task ChangeSignature_RemoveParameter_UpdatesAllCallers() { }
    [Fact] public async Task ChangeSignature_ReorderParameters_UpdatesAllCallers() { }
    [Fact] public async Task ChangeSignature_RazorCallersOfCsMethod_UpdatedCorrectly() { }
    [Fact] public async Task ChangeSignature_CsCallersOfRazorMethod_UpdatedCorrectly() { }
    
    // ---- implement_missing_members ----
    
    [Fact] public async Task ImplementMissingMembers_AbstractBase_GeneratesStubs() { }
    [Fact] public async Task ImplementMissingMembers_Interface_GeneratesStubs() { }
    [Fact] public async Task ImplementMissingMembers_StubsInCodeBlock() { }
    [Fact] public async Task ImplementMissingMembers_AlreadyImplemented_ReturnsEmpty() { }
}
```

### L2-SYNC: sync_documents Tests

```csharp
public class RazorSyncTests
{
    [Fact] public async Task SyncDocuments_RazorFileChanged_RegeneratesVirtualDoc() { }
    [Fact] public async Task SyncDocuments_RazorFileAdded_AddsVirtualDoc() { }
    [Fact] public async Task SyncDocuments_RazorFileDeleted_RemovesVirtualDoc() { }
    [Fact] public async Task SyncDocuments_RazorFileRenamed_UpdatesRegistry() { }
    [Fact] public async Task SyncDocuments_AfterRazorSync_SymbolsUpdated() { }
    [Fact] public async Task SyncDocuments_MultipleRazorFiles_AllSynced() { }
    [Fact] public async Task SyncDocuments_RazorAndCsFilesSyncedTogether() { }
    [Fact] public async Task SyncDocuments_SpecificRazorFile_SyncsOnlyThatFile() { }
    [Fact] public async Task SyncDocuments_AllFiles_IncludesRazorFiles() { }
    [Fact] public async Task SyncDocuments_AfterSync_QueryReturnsUpdatedValues() { }
    [Fact] public async Task SyncDocuments_CacheClearedAfterRazorSync() { }
}
```

### L2-DISC: Discovery Tools on Razor

```csharp
public class RazorDiscoveryTests
{
    [Fact] public async Task GetProjectStructure_RazorProject_ShowsRazorFileCount() { }
    [Fact] public async Task GetProjectStructure_RazorProject_HasIsRazorProjectTrue() { }
    [Fact] public async Task GetProjectStructure_NonRazorProject_HasIsRazorProjectFalse() { }
    [Fact] public async Task GetProjectStructure_RazorFilesInDocumentList() { }
    [Fact] public async Task GetDiRegistrations_InjectDirective_FindsServiceRegistration() { }
    [Fact] public async Task GetDiRegistrations_MultipleInject_FindsAll() { }
    [Fact] public async Task FindReflectionUsage_RazorCodeBlock_DetectedIfPresent() { }
    [Fact] public async Task GetNugetDependencies_RazorProject_IncludesAspNetCoreNugets() { }
}
```

### L2-QUAL: Quality Tools on Razor

```csharp
public class RazorQualityTests
{
    [Fact] public async Task FindUnusedCode_RazorPrivateMethod_DetectedIfUnused() { }
    [Fact] public async Task FindUnusedCode_RazorPublicParameter_NotFlagged() { }
    [Fact] public async Task FindUnusedCode_ExcludesGeneratedRenderTreeMethods() { }
    [Fact] public async Task FindGodObjects_RazorComponents_IncludedInScan() { }
    [Fact] public async Task FindUntestedCode_RazorPublicMethods_Included() { }
    [Fact] public async Task GetComplexityMetrics_RazorMethod_MeasuredCorrectly() { }
    [Fact] public async Task FindAttributeUsages_RazorWithAttribute_Found() { }
    [Fact] public async Task FindAttributeUsages_AuthorizeInRazor_Found() { }
}
```

### L2-COMPOUND: Compound Tools on Razor

```csharp
public class RazorCompoundTests
{
    [Fact] public async Task GetTypeOverview_RazorComponent_ReturnsFullInfo() { }
    [Fact] public async Task GetTypeOverview_IncludesRazorFilePath() { }
    [Fact] public async Task AnalyzeMethod_RazorMethod_ReturnsSignatureAndCallers() { }
    [Fact] public async Task GetFileOverview_RazorFile_ReturnsRazorPath() { }
    [Fact] public async Task GetMethodSource_RazorMethod_ReturnsGeneratedCSharpWithNote() { }
    [Fact] public async Task GetMethodSourceBatch_MixedCsRazor_ReturnsAll() { }
    [Fact] public async Task GetProjectHealth_RazorProject_IncludesRazorDiagnostics() { }
}
```

### L2-CODEACTIONS: Code Actions on Razor

```csharp
public class RazorCodeActionTests
{
    [Fact] public async Task GetCodeActionsAtPosition_RazorCodeBlock_ReturnsActions() { }
    [Fact] public async Task GetCodeActionsAtPosition_RazorMarkup_ReturnsEmpty() { }
    [Fact] public async Task ApplyCodeActionByTitle_RazorField_EncapsulateField() { }
    [Fact] public async Task ApplyCodeActionByTitle_ResultInRazorFileFormat() { }
    [Fact] public async Task GetCodeFixes_RazorError_ReturnsFixes() { }
    [Fact] public async Task ApplyCodeFix_RazorError_FixesFile() { }
}
```

### L2-EXTERNAL: External API on Razor

```csharp
public class RazorExternalApiTests
{
    [Fact] public async Task GetExternalTypeInfo_ComponentBase_ReturnsMembers() { }
    // Razor-generated types should be discoverable via external type info if needed
}
```

### L2-VALIDATE: Validate Code with Razor

```csharp
public class RazorValidateTests
{
    [Fact] public async Task ValidateCode_RazorSnippet_CompilesInContext() { }
    [Fact] public async Task ValidateCode_InvalidRazorSyntax_ReturnsErrors() { }
    [Fact] public async Task ValidateCode_RazorWithContextFile_GetsContextSymbols() { }
}
```

---

## L3 — Solution Integration Tests (Phase G)

These load a real Blazor project via MSBuildWorkspace. They validate the end-to-end pipeline.

### Test Project

```
tests/SharpLensMcp.Tests.RazorFixture/
  SharpLensMcp.Tests.RazorFixture.csproj
  Pages/
    Counter.razor
    Counter.razor.cs
    FetchData.razor
    Index.razor
  Shared/
    NavMenu.razor
    SurveyPrompt.razor
  _Imports.razor
  App.razor
```

### Test Class

```csharp
public class RazorIntegrationTests : RoslynServiceTestBase
{
    // Override InitializeAsync to load the Blazor fixture solution instead
    
    // ---- End-to-end: load → query → verify ----
    
    [Fact] public async Task LoadSolution_DiscoversRazorFiles() { }
    [Fact] public async Task LoadSolution_RazorFilesInProjectStructure() { }
    [Fact] public async Task LoadSolution_DoesNotCrash_WithRealBlazorProject() { }
    
    // ---- Navigation on real project ----
    
    [Fact] public async Task GetSymbolInfo_CounterField_ReturnsField() { }
    [Fact] public async Task GoToDefinition_CounterMethod_JumpsToDeclaration() { }
    [Fact] public async Task FindReferences_CounterField_FindsAll() { }
    
    // ---- Code-behind ----
    
    [Fact] public async Task FindReferences_CodeBehindField_FindsRazorUsages() { }
    [Fact] public async Task GoToDefinition_RazorCall_JumpsToCodeBehind() { }
    
    // ---- sync_documents on real project ----
    
    [Fact] public async Task SyncDocuments_AfterRazorEdit_UpdatesResults() { }
    [Fact] public async Task SyncDocuments_AfterAddingRazor_NewFileQueryable() { }
    [Fact] public async Task SyncDocuments_AfterDeletingRazor_SymbolsGone() { }
    
    // ---- Edit → sync → query cycle ----
    
    [Fact] public async Task EditRazorFile_Sync_QueryReturnsUpdatedSymbols() { }
    [Fact] public async Task RenameSymbol_Apply_UpdateCsproj_Rebuild_AllConsistent() { }
}
```

---

## L4 — Refactoring Apply Tests

### Snapshot/Restore Pattern

```csharp
public abstract class RazorRefactoringApplyTestBase : IAsyncLifetime
{
    private string _tempDir;
    private Dictionary<string, string> _snapshots;
    
    protected string SnapshotFile(string relativePath)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        _snapshots[fullPath] = File.ReadAllText(fullPath);
        return fullPath;
    }
    
    public Task DisposeAsync()
    {
        foreach (var (path, content) in _snapshots)
            File.WriteAllText(path, content);
        Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }
}
```

### Apply Tests

```csharp
public class RazorRefactoringApplyTests : RazorRefactoringApplyTestBase
{
    [Fact] public async Task RenameSymbol_Apply_WritesToDisk() { }
    [Fact] public async Task RenameSymbol_Apply_FileContainsNewName() { }
    [Fact] public async Task RenameSymbol_Apply_FileDoesNotContainOldName() { }
    [Fact] public async Task RenameSymbol_Apply_AllOccurrencesRenamed() { }
    [Fact] public async Task RenameSymbol_Apply_MarkupUnchanged() { }
    [Fact] public async Task RenameSymbol_Apply_RegeneratedCSharpContainsNewName() { }
    [Fact] public async Task RenameSymbol_Apply_CanBeRenamedAgain() { }
    
    [Fact] public async Task ExtractMethod_Apply_WritesToDisk() { }
    [Fact] public async Task ExtractMethod_Apply_NewMethodInCodeBlock() { }
    [Fact] public async Task ExtractMethod_Apply_CallSiteReplaced() { }
    
    [Fact] public async Task ImplementMissingMembers_Apply_StubsInCodeBlock() { }
    
    [Fact] public async Task ChangeSignature_Apply_AllCallersUpdated() { }
    
    // ---- Apply failure scenarios ----
    
    [Fact] public async Task Apply_ThenQuery_ReturnsConsistentResults() { }
    [Fact] public async Task Apply_FailsGracefully_RestoresSnapshot() { }
    [Fact] public async Task Apply_OnReadOnlyFile_ReturnsError() { }
}
```

---

## Regression Test Checklist

Must pass after every change:

- [ ] All 543 existing SharpLensMcp tests
- [ ] L1-MAP: all mapping tests
- [ ] L1-DOC: all document resolution tests
- [ ] L2-NAV: 9 navigation tools × at least 3 razor scenarios each
- [ ] L2-ANALYSIS: 6 analysis tools × at least 2 razor scenarios each
- [ ] L2-TYPE: 10 type tools × at least 1 razor scenario each
- [ ] L2-REF: 7 refactoring tools × preview + apply
- [ ] L2-SYNC: all sync scenarios
- [ ] L2-DISC: all discovery scenarios
- [ ] L2-QUAL: all quality scenarios
- [ ] L2-COMPOUND: all compound scenarios
- [ ] L2-CODEACTIONS: all code action scenarios
- [ ] L3: integration suite against real Blazor project
- [ ] L4: apply tests with snapshot/restore
- [ ] Performance: 50 .razor files process in < 3 seconds
- [ ] Memory: no leak after repeated sync_documents cycles
- [ ] Concurrent: no crash if watcher fires during tool execution (debounce handles this)

---

## New Edge Cases Discovered

_Add here as found during implementation. Format: `[Date] [Author] Description`_

| # | Date | Found by | Description | Test Added |
|---|---|---|---|---|
| 1 | — | — | _(template)_ | — |
