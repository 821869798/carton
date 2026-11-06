using System.Reflection;
using carton.Core.Services;
using carton.ViewModels;

internal static class JsonValidationProbe
{
    internal static void Run()
    {
        using var vm = new ProfilesViewModel();
        var manager = DispatchProxy.Create<ISingBoxManager, ManagerProxy>();
        var proxy = (ManagerProxy)(object)manager;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ProfilesViewModel).GetField("_singBoxManager", flags)!.SetValue(vm, manager);
        string? toast = null;
        typeof(ProfilesViewModel).GetField("_toastWriter", flags)!.SetValue(vm, (Action<string,int>)((message, _) => toast = message));
        foreach (var text in new[] { "[\"zxc\",]", "{\"a\":1,}", "[1,,2]", "{\"a\":/*comment*/1}", "{\"a\":" })
        {
            vm.ConfigContent = text; toast = null;
            vm.ValidateConfigJsonCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (proxy.Checks != 0 || string.IsNullOrEmpty(toast) || vm.ConfigDocument.ErrorLine == null) throw new Exception("Invalid JSON not marked or was sent to permissive kernel: " + text);
        }
        foreach (var text in new[] { "[\"zxc\"]", "{\"a\":1}", "{\"a\":\",]\"}" })
        {
            vm.ConfigContent = text; toast = null;
            var before = proxy.Checks;
            vm.ValidateConfigJsonCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (proxy.Checks != before + 1 || proxy.LastText != text || string.IsNullOrEmpty(toast) || vm.ConfigDocument.ErrorLine != null) throw new Exception("Valid JSON did not reach kernel unchanged or retained error mark");
        }
        vm.ConfigContent = "{\n  \"名字\": [\"zxc\",]\n}";
        vm.ValidateConfigJsonCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (vm.ConfigDocument.ErrorLine != 1) throw new Exception("Wrong zero-based error line");
        var comma = vm.ConfigContent.IndexOf(",]", StringComparison.Ordinal);
        vm.ConfigDocument.Replace(comma, 1, "");
        if (vm.ConfigDocument.ErrorLine != null) throw new Exception("Edit retained stale diagnostic");
        vm.ValidateConfigJsonCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (vm.ConfigDocument.ErrorLine != null) throw new Exception("Corrected document still marked");
        Console.WriteLine("PASS JSON error line, edit invalidation and corrected revalidation");
        Console.WriteLine("PASS strict JSON before permissive kernel: trailing/repeated commas, comments, malformed syntax and valid string literals");
    }
    public class ManagerProxy : DispatchProxy
    {
        public int Checks;
        public string? LastText;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "CheckConfigAsync")
            {
                Checks++; LastText = (string)args![0]!;
                return Task.FromResult((true, "Permissive kernel accepts input"));
            }
            if (method.Name.StartsWith("remove_", StringComparison.Ordinal)) return null;
            throw new NotSupportedException(method.Name);
        }
    }
}
