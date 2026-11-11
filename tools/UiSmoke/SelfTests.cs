internal static partial class Program
{
    private static int SelfTest()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            count++;
            if (!condition) throw new InvalidOperationException("UI audit regression: " + message);
        }
        string Wrap(string content) => $"<Grid xmlns='{Presentation}' xmlns:p='clr-namespace:PCL' xmlns:w='{Presentation}'>{content}</Grid>";
        foreach (string control in NativeControls)
        {
            Check(InspectXaml("fixture.xaml", Wrap($"<{control}/> ")).Single().Control == control, control + " XAML");
            Check(InspectXaml("fixture.xaml", Wrap($"<w:{control}/> ")).Single().Control == control, control + " alias");
        }
        Check(!InspectXaml("fixture.xaml", Wrap("<!-- <Button/> --><p:MyButton/><TextBlock/><Border/> ")).Any(), "PCL controls, comments and layout are allowed");
        Check(InspectXaml("fixture.xaml", Wrap("<Button Style='{DynamicResource Anything}'/>")).Count() == 1, "Style does not exempt a native button");
        Check(!InspectXaml("fixture.xaml", Wrap("<ControlTemplate TargetType='Button'><Border/></ControlTemplate>")).Any(), "TargetType is not an instantiation");
        Check(InspectXaml("fixture.xaml", Wrap("<ControlTemplate><Button/></ControlTemplate>")).Count() == 1, "Templates are not blanket exempted");
        Check(InspectXaml("fixture.xaml", Wrap("\n<Button/> ")).Single().Line == 2, "XAML line numbers");
        foreach (string configuration in Configurations)
        {
            var basic = InspectCSharp("fixture.cs", "class Test { object M() => new System.Windows.Controls.Button(); }", configuration).ToArray();
            Check(basic.Length == 1 && basic[0].Control == "Button", configuration + " qualified constructor");
            Check(InspectCSharp("fixture.cs", "using B = System.Windows.Controls.Button; class T { B b = new(); }", configuration).Count() == 1, configuration + " target-typed alias");
            Check(InspectCSharp("fixture.cs", "using B = System.Windows.Controls.Button; class T { object M() => new B(); }", configuration).Count() == 1, configuration + " alias constructor");
            Check(InspectCSharp("fixture.cs", "class T { Button M() => new(); }", configuration).Count() == 1, configuration + " target-typed return");
            Check(InspectCSharp("fixture.cs", "class T { Button B { get; set; } = new(); }", configuration).Count() == 1, configuration + " target-typed property");
            Check(InspectCSharp("fixture.cs", "class T { Button b; void M() { b = new(); } }", configuration).Count() == 1, configuration + " target-typed assignment");
            Check(InspectCSharp("fixture.cs", "class T { void N(Button b) {} void M() { N(new()); } }", configuration).Count() == 1, configuration + " target-typed argument");
            Check(InspectCSharp("fixture.cs", "class T { Button[] B = [new()]; }", configuration).Count() == 1, configuration + " collection expression");
            Check(!InspectCSharp("fixture.cs", "class MyButton : Button {} class T { object M() => new MyButton(); }", configuration).Any(), configuration + " custom PCL subclass");
            Check(!InspectCSharp("fixture.cs", "namespace Other { class Button {} class T { object M() => new Button(); } }", configuration).Any(), configuration + " unrelated Button class");
            Check(!InspectCSharp("fixture.cs", "class T { string s = \"new Button()\"; /* new Button(); */ }", configuration).Any(), configuration + " comments and strings");
            Check(InspectCSharp("fixture.cs", "#if " + configuration + "\nclass T { object M() => new Button(); }\n#endif", configuration).Count() == 1, configuration + " active conditional");
            Check(!InspectCSharp("fixture.cs", "#if UNDEFINED\nclass T { object M() => new Button(); }\n#endif", configuration).Any(), configuration + " inactive conditional");
            string inline = "class T { object M() => System.Windows.Markup.XamlReader.Parse(\"<Button xmlns='" + Presentation + "'/>\"); }";
            Check(InspectCSharp("fixture.cs", inline, configuration).Count() == 1, configuration + " constant runtime XAML");
        }
        try { InspectXaml("fixture.xaml", "<Grid>").ToArray(); throw new InvalidOperationException("Malformed XML passed"); }
        catch (System.Xml.XmlException) { count++; }
        Check(Print(InspectXaml("fixture.xaml", Wrap("<Button/>")), report: false) == 1, "violations must return an error exit code");
        Check(Print([], report: false) == 0, "valid UI returns zero");
        var existing = InspectXaml("fixture.xaml", Wrap("<Button/>")).Single();
        var approved = new ExceptionEntry(existing.Path, existing.Control, existing.Fingerprint, 1, "Test fixture");
        Check(WithoutApproved([existing], [approved]).Count == 0, "exact approved element");
        Check(WithoutApproved([existing], []).Count == 1, "unapproved element remains an error");
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (System.IO.InvalidDataException) { Check(true, message); return; }
            Check(false, message);
        }
        Reject(() => WithoutApproved([existing, existing], [approved]), "duplicating an exempt element must fail");
        Reject(() => WithoutApproved([], [approved]), "deleted exception must fail");
        Reject(() => WithoutApproved([existing with { Fingerprint = new string('0', 64) }], [approved]), "changing exempt content must fail");
        Reject(() => WithoutApproved([existing with { Path = "other.xaml" }], [approved]), "moving an exempt element must fail");
        Reject(() => WithoutApproved([existing], [approved, approved]), "duplicate exception must fail");
        Reject(() => WithoutApproved([existing], [approved with { Reason = "" }]), "unexplained exception must fail");
        Console.WriteLine($"UI smoke analyzer: {count} regression checks passed.");
        return 0;
    }
}
