using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Binding;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TACTTool
{
    internal static class Extensions
    {
        public static void AddGlobalOptions(this Command command, params ReadOnlySpan<Option> options)
        {
            foreach (var option in options)
                command.AddGlobalOption(option);
        }

        public static T GetService<T>(this BindingContext context) where T : class
        {
            return (T) context.GetService(typeof(T))!;
        }
    }
}
