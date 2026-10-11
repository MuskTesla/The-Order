using TheOrder.Platform.Windows;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
if(args.Length!=1)throw new ArgumentException("Expected native Assets directory.");
var root=Path.GetFullPath(args[0]);var source=await File.ReadAllBytesAsync(Path.Combine(root,"Brand.png"));var codec=new WindowsRasterCodec();
foreach(var light in new[]{true,false}) {
    // TitleBar creates a separate icon visual and can retain BitmapIcon's startup
    // foreground. Supply explicit pixels for the ordinary Light/Dark silhouette.
    var png=await codec.DecodeAsync(source,256,default);
    for(int n=0;n<png.Bgra.Length;n+=4) {
        var value=light?(byte)0:(byte)((245*png.Bgra[n+3]+127)/255);
        png.Bgra[n]=value;png.Bgra[n+1]=value;png.Bgra[n+2]=value;
    }
    using(var stream=new InMemoryRandomAccessStream()) {
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)png.Width,(uint)png.Height,96,96,png.Bgra);
        await encoder.FlushAsync();var bytes=new byte[(int)stream.Size];stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(),(uint)bytes.Length,InputStreamOptions.None);
        await File.WriteAllBytesAsync(Path.Combine(root,light?"Brand.Light.png":"Brand.Dark.png"),bytes);
    }
    var frames=new List<byte[]>();var sizes=new[]{16,24,32,48,64,128,256};
    foreach(var size in sizes) {
        var image=await codec.DecodeAsync(source,size,default);if(image.Width!=size||image.Height!=size)throw new InvalidDataException("Square brand asset required.");
        using var data=new MemoryStream();using var writer=new BinaryWriter(data);var maskStride=((size+31)/32)*4;
        writer.Write(40);writer.Write(size);writer.Write(size*2);writer.Write((short)1);writer.Write((short)32);writer.Write(0);writer.Write(size*size*4+maskStride*size);for(int n=0;n<4;n++)writer.Write(0);
        for(int y=size-1;y>=0;y--)for(int x=0;x<size;x++){var a=image.Bgra[(y*size+x)*4+3];var value=light?(byte)0:(byte)245;writer.Write(value);writer.Write(value);writer.Write(value);writer.Write(a);}
        writer.Write(new byte[maskStride*size]);frames.Add(data.ToArray());
    }
    using var output=File.Create(Path.Combine(root,light?"Brand.Light.ico":"Brand.Dark.ico"));using var icon=new BinaryWriter(output);icon.Write((short)0);icon.Write((short)1);icon.Write((short)sizes.Length);var offset=6+sizes.Length*16;
    for(int n=0;n<sizes.Length;n++){var s=(byte)(sizes[n]==256?0:sizes[n]);icon.Write(s);icon.Write(s);icon.Write((byte)0);icon.Write((byte)0);icon.Write((short)1);icon.Write((short)32);icon.Write(frames[n].Length);icon.Write(offset);offset+=frames[n].Length;}
    foreach(var frame in frames)icon.Write(frame);
}
Console.WriteLine("Generated black/light icons from the original alpha silhouette; no replacement artwork.");
