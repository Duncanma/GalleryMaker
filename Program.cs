using ImageMagick;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GalleryMaker
{
    internal class Program
    {
        static void Main(string[] args)
        {
            //determine if the provided folder is a folder of folders or of images

            if (args.Length != 3)
            {
                Console.WriteLine("Invalid number of arguments supplied");
            }

            string inputPath = args[0];
            string outputPath = args[1];
            string baseURL = args[2];

            if (baseURL.EndsWith("/"))
            {
                baseURL = baseURL.Substring(0, baseURL.Length - 1);
            }
            var subFolders = Directory.GetDirectories(inputPath);
            var options = new JsonSerializerOptions { WriteIndented = true };
            if (subFolders.Length > 0 )
            {
                AlbumGroup group = new AlbumGroup() { Description = "", Title = Path.GetFileName(inputPath), Albums = new List<Album>() };

                //it's a set of folders
                foreach ( var subFolder in subFolders )
                {
                    var folderName = Path.GetFileName(subFolder).ToLower();
                    Album album = new Album() { Description = "", Title = folderName, Pictures = new List<Picture>(), BaseURL = baseURL + "/" + folderName };
                    List<Picture> pictures = ProcessAlbum(subFolder, Path.Combine(outputPath, folderName), album.BaseURL);
                    album.Pictures.AddRange(pictures);
                    group.Albums.Add(album);
                }
                string output = JsonSerializer.Serialize(group, options);
                Console.WriteLine(output);
                File.WriteAllText(Path.Combine(outputPath, "group.json"), output);
            }
            else
            {
                var folderName = Path.GetFileName(inputPath).ToLower(); ;
                Album album = new Album() { Description = "", Title = folderName, Pictures = new List<Picture>(), BaseURL = baseURL };
                List<Picture> pictures = ProcessAlbum(inputPath, outputPath, baseURL);
                album.Pictures.AddRange(pictures);
                string output = JsonSerializer.Serialize(album, options);
                Console.WriteLine(output);
                File.WriteAllText(Path.Combine(outputPath, "album.json"), output);
            }
        }

        private static List<Picture> ProcessAlbum(string subFolder, string outputPath, string baseURL)
        {
            List<Picture> pictures = new List<Picture>();
            var files = Directory.GetFiles(subFolder,"*.jpg");
            foreach (var file in files)
            {
                string fileName = Path.GetFileName(file);
                string outputFileName = Path.Combine(outputPath, fileName).ToLower();
                //now get the resolution of the image, get the title and caption from the EXIF

                string title, caption;
                title = Path.GetFileNameWithoutExtension(file);
                var imageFromFile = new MagickImage(file);

                var exif = imageFromFile.GetExifProfile();

                string camera = "";
                string lens = "";
                string focalLength = "";
                string fValue = "";
                DateTimeOffset? imageDate = null;

                if (exif != null)
                {
                    //var values = exif.Values.ToList();

                    camera = getCameraName(exif);
                    lens = getLensInfo(exif);

                    focalLength = "";

                    //if it's a zoom lens
                    bool zoomLens = Regex.IsMatch(lens, "\\d.-\\d.mm");


                    if (zoomLens)
                    {
                        var Focal = exif.GetValue(ExifTag.FocalLength);
                        if (Focal != null)
                        {
                            focalLength = $"{Focal.Value.ToString()}";
                            focalLength = FlattenFraction(focalLength);

                        }
                    }

                    fValue = getfValue(exif);


                    imageDate = getImageDateTime(exif);

                    if (imageDate != null)
                    {
                        Console.WriteLine(imageDate.ToString());
                    }


                }


                var iptc = imageFromFile.GetIptcProfile();

                title = iptc?.GetValue(IptcTag.Title)?.Value ?? title;
                caption = iptc?.GetValue(IptcTag.Caption)?.Value ?? "";

                var pic = new Picture() { Caption = caption, 
                    Title = title, 
                    Latitude = "", Longitude = "", 
                    Links = new List<Link>(), 
                    Camera = camera, 
                    FocalLength = focalLength, 
                    fStop = fValue, 
                    Lens=lens,
                    DateTimeOriginal = imageDate };
                pic.Links.Add(ResizeAndSaveFile(outputPath, file, imageFromFile, 2160, baseURL));
                pic.Links.Add(ResizeAndSaveFile(outputPath, file, imageFromFile, 1080, baseURL));
                pic.Links.Add(ResizeAndSaveFile(outputPath, file, imageFromFile, 540, baseURL));
                pic.Links.Add(ResizeAndSaveFile(outputPath, file, imageFromFile, 220, baseURL));
                pictures.Add(pic);

            }
            return pictures;
        }

        private static Link ResizeAndSaveFile(string outputPath, string file, MagickImage imageFromFile, int width, string baseURL)
        {
            string newFileName = (Path.GetFileNameWithoutExtension(file) +"-"+ width.ToString() +  ".jpg").ToLower();
            string newFilePath = Path.Combine(outputPath, newFileName);
            imageFromFile.Resize(width, 0);
            imageFromFile.Write(newFilePath);

            var link = new Link(){ Height = imageFromFile.Height, Width = imageFromFile.Width, Url = baseURL + "/" + newFileName};
            var i = new ImageOptimizer();
            i.OptimalCompression = true;
            i.Compress(newFilePath);
            return link;
        }


        private static DateTimeOffset? getImageDateTime(IExifProfile exif)
        {
            string imageDateTime;
            string imageOffset = "";
            DateTimeOffset? dateTime = null;

            var DateOriginal = exif.GetValue(ExifTag.DateTimeOriginal);

            if (DateOriginal != null)
            {
                imageDateTime = DateOriginal.Value.ToString();
                var OffsetOriginal = exif.GetValue(ExifTag.OffsetTimeOriginal);
                if (OffsetOriginal != null)
                {
                    imageOffset = OffsetOriginal.Value.ToString();
                    imageDateTime = imageDateTime + " " + imageOffset;
                    dateTime = DateTimeOffset.ParseExact(imageDateTime, "yyyy:MM:dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
                }
                else
                {
                    dateTime = DateTimeOffset.ParseExact(imageDateTime, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
                }



            }

            return dateTime;

        }

        private static string getfValue(IExifProfile exif)
        {
            string fValue = "";
            var FNumber = exif.GetValue(ExifTag.FNumber);

            if (FNumber != null)
            {
                fValue = $"{FNumber.Value.ToString()}";
            }
            fValue = FlattenFraction(fValue);
            return fValue;
        }

        private static string getLensInfo(IExifProfile exif)
        {
            string lensInfo = "";
            var Make = exif.GetValue(ExifTag.LensMake);
            var Model = exif.GetValue(ExifTag.LensModel);

            if (Make != null)
            {
                lensInfo = Make.Value;
            }

            if (Model != null)
            {
                if (lensInfo != "")
                {
                    lensInfo += " ";
                }
                string model = Model.Value;

                model = model.Replace("mmF", "mm F");

                lensInfo += model;

            }

            return lensInfo;
        }

        private static string getCameraName(IExifProfile exif)
        {
            string cameraName = "";
            string cameraMake = "";
            string cameraModel = "";

            var Make = exif.GetValue(ExifTag.Make);
            var Model = exif.GetValue(ExifTag.Model);


            if (Make != null)
            {
                cameraMake = Make.Value;
            }

            if (Model != null)
            {
                cameraModel = Model.Value;
            }


            if (!String.IsNullOrWhiteSpace(cameraMake))
            {
                if (!String.IsNullOrWhiteSpace(cameraModel))
                {
                    if (cameraModel.Contains(cameraMake))
                    {
                        cameraName = cameraModel;
                    } 
                    else
                    {
                        cameraName = $"{cameraMake} {cameraModel}";
                    }
                }
                else
                {
                    cameraName = cameraMake;
                }
            }

            return cameraName;
        }

        private static string FlattenFraction(string fraction)
        {
            if (fraction.Contains("/"))
            {
                int denominator = int.Parse(fraction.Substring(fraction.IndexOf("/") + 1));
                int numerator = int.Parse(fraction.Substring(0, fraction.IndexOf("/")));

                double rationalValue = numerator / (double)denominator;
                fraction = rationalValue.ToString();
            }
            return fraction;
        }

    }
}
