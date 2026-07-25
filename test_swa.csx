using System;
using System.Diagnostics;
using System.Windows.Automation;

// 找微信进程
Process[] weChatProcesses = Process.GetProcessesByName("WeChat");
Console.WriteLine($"找到 {weChatProcesses.Length} 个微信进程");

if (weChatProcesses.Length > 0)
{
    foreach (var p in weChatProcesses)
    {
        Console.WriteLine($"  PID: {p.Id}, MainWindowHandle: {p.MainWindowHandle}, Title: {p.MainWindowTitle}");
        
        if (p.MainWindowHandle != IntPtr.Zero)
        {
            try
            {
                var element = AutomationElement.FromHandle(p.MainWindowHandle);
                Console.WriteLine($"  SWA FromHandle 成功: {element.Current.Name}, ControlType: {element.Current.ControlType.ProgrammaticName}");
                
                var walker = TreeWalker.ControlViewWalker;
                var child = walker.GetFirstChild(element);
                int count = 0;
                while (child != null && count < 20)
                {
                    Console.WriteLine($"    子元素 {count}: {child.Current.ControlType.ProgrammaticName} Name={child.Current.Name}");
                    child = walker.GetNextSibling(child);
                    count++;
                }
                Console.WriteLine($"  ControlView 子元素总数: {count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  SWA 异常: {ex.Message}");
            }
        }
    }
}
else
{
    Console.WriteLine("未找到微信进程");
}
