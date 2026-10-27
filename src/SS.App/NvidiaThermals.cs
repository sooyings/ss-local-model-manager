// Read-only NVAPI adapter. Thermal interface layout and RTX 30/40 sensor mapping:
// https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Interop/NvApi.cs
// https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs
// No driver installation, fan control, voltage or clock writes.
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace ModelConsole {
 public sealed class NvidiaThermals {
  [StructLayout(LayoutKind.Sequential,Pack=8)]struct ThermalBlock {
   public uint Version,Mask;
   [MarshalAs(UnmanagedType.ByValArray,SizeConst=8)]public int[] Reserved;
   [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)]public int[] Readings;
  }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Init();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Enumerate([Out]IntPtr[] handles,out int count);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int ReadThermals(IntPtr gpu,ref ThermalBlock data);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int ReadName(IntPtr gpu,StringBuilder name);
  [DllImport("nvapi64.dll",EntryPoint="nvapi_QueryInterface",CallingConvention=CallingConvention.Cdecl)]
  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  static extern IntPtr Query(uint id);
  static T Function<T>(uint id)where T:class {var p=Query(id);return p==IntPtr.Zero?null:Marshal.GetDelegateForFunctionPointer(p,typeof(T)) as T;}
  bool initialized;IntPtr gpu;ReadThermals read;uint mask;int memoryIndex=-1;
  public double? Core,Hotspot,Memory;
  public string Status="Waiting for NVAPI";
  public static double? Celsius(int raw){double n=raw/256.0;return n>0&&n<150?n:(double?)null;}
  ThermalBlock Block(uint sensors){return new ThermalBlock{Version=(uint)(Marshal.SizeOf(typeof(ThermalBlock))|(2<<16)),Mask=sensors,Reserved=new int[8],Readings=new int[32]};}
  void Initialize(){
   initialized=true;var init=Function<Init>(0x0150E828);if(init==null||init()!=0){Status="NVAPI unavailable";return;}
   var enumerate=Function<Enumerate>(0xE5AC921F);var name=Function<ReadName>(0xCEEE8E9F);read=Function<ReadThermals>(0x65FE3AAD);
   if(enumerate==null||name==null||read==null){Status="Thermal API unavailable";return;}
   var handles=new IntPtr[64];int count;if(enumerate(handles,out count)!=0||count!=1){Status="Single NVIDIA GPU required for verified sensor mapping";return;}gpu=handles[0];
   var text=new StringBuilder(64);if(name(gpu,text)!=0){gpu=IntPtr.Zero;return;}
   string model=text.ToString();if(model.Contains("RTX 30"))memoryIndex=9;else if(model.Contains("RTX 40"))memoryIndex=7;else {Status="Thermal mapping not verified for this GPU";gpu=IntPtr.Zero;return;}
   for(int bit=0;bit<32;bit++){uint flag=1u<<bit;var probe=Block(flag);if(read(gpu,ref probe)!=0){mask=flag-1;break;}mask=flag;}
   Status=mask>0?"NVIDIA NVAPI · independent sensors":"Thermal sensors unavailable";
  }
  public void Poll(){
   Core=Hotspot=Memory=null;
   try{if(!initialized)Initialize();if(gpu==IntPtr.Zero||read==null||mask==0)return;
    var data=Block(mask);if(read(gpu,ref data)!=0){Status="Thermal read failed; retrying";return;}
    if((mask&1)!=0)Core=Celsius(data.Readings[0]);
    if((mask&2)!=0)Hotspot=Celsius(data.Readings[1]);
    if(memoryIndex>=0&&(mask&(1u<<memoryIndex))!=0)Memory=Celsius(data.Readings[memoryIndex]);
    Status="NVIDIA NVAPI · independent sensors";
   }catch(Exception ex){Status="NVAPI "+ex.GetType().Name;}
  }
 }
}
