using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ImageMagick;
using Microsoft.Extensions.Configuration;
using Stripe;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using File = System.IO.File;

namespace GalleryMaker
{
    internal class Program
    {
        static string stripeKey;
        static string hashKey;
        static string azureConnectionString;

        static string[] notForSaleProducts = [
            "f40cc48ede9c198205d81b8b240bdb00",
            "e4c0fbc87e219b760bb436983fc59f62",
            "93c8d183d589c178878fc954bb2b9633",
            "55357baa98c5210a7271144b6b2a09c7",
            "f679f80d3a5c3acb98a5255698d676eb",
            "d5f304d795096f00b8b52838288c05b0"
        ];

        static bool doFileCopyandUpload = true;
        static bool doStripeStuff = true;
        static bool updateProducts = true;

        static List<Album> albumList = new List<Album>();
        static Dictionary<string, Picture> pictureLookup = new Dictionary<string, Picture>();

        static void Main(string[] args)
        {

            LoadPictures("C:\\Repos\\Blog\\content\\albums");

            //fetch secrets, more information on this style of secret storage/retrieval is at 
            //https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-8.0&tabs=windows#user-secrets-in-non-web-applications
            IConfigurationRoot config = new ConfigurationBuilder()
                .AddUserSecrets<Program>()
                .Build();

            stripeKey = config["stripekey"];
            hashKey = config["hashstring"];
            azureConnectionString = config["AzureConnectionString"];

            //using the .NET SDK for Stripe: https://github.com/stripe/stripe-dotnet 
            StripeConfiguration.ApiKey = stripeKey;
            StripeConfiguration.AppInfo = new AppInfo() { Name = "Gallery Maker" };

            //load all my products
            if (doStripeStuff)
            {
                LoadProducts();
            }


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

                    Album album = new Album() { Description = "", 
                        Title = folderName, 
                        Pictures = new List<Picture>(), 
                        BaseURL = baseURL + "/" + folderName 
                    };
                    List<Picture> pictures = ProcessAlbum(subFolder, Path.Combine(outputPath, folderName), album.BaseURL);
                    album.Pictures.AddRange(pictures);
                    if (pictures.Exists(p => p.PaymentLink != null))
                    {
                        album.Outputs = ["html", "purchase"];
                    }
                    var existingAlbum = albumList.Find(a => a.BaseURL == baseURL);
                    if (existingAlbum != null)
                    {
                        album.Description = existingAlbum.Description;
                        album.Title = existingAlbum.Title;
                        album.Featured = existingAlbum.Featured;
                    }
                    group.Albums.Add(album);
                }
                string output = JsonSerializer.Serialize(group, options);
                Console.WriteLine(output);
                File.WriteAllText(Path.Combine(outputPath, "group.json"), output);
            }
            else
            {
                var folderName = Path.GetFileName(inputPath).ToLower();

                Album album = new Album() { Description = "", 
                    Title = folderName, 
                    Pictures = new List<Picture>(), 
                    BaseURL = baseURL 
                };
                List<Picture> pictures = ProcessAlbum(inputPath, outputPath, baseURL);
                album.Pictures.AddRange(pictures);
                if (pictures.Exists(p => p.PaymentLink != null))
                {
                    album.Outputs = ["html", "purchase"];
                }

                var existingAlbum = albumList.Find(a => a.BaseURL == baseURL);
                if (existingAlbum != null)
                {
                    album.Description = existingAlbum.Description;
                    album.Title = existingAlbum.Title;
                    album.Featured = existingAlbum.Featured;
                }

                string output = JsonSerializer.Serialize(album, options);
                Console.WriteLine(output);
                File.WriteAllText(Path.Combine(outputPath, "album.json"), output);
            }
        }

        private static void LoadPictures(string path)
        {
            var albumFiles = Directory.GetFiles(path, "*.md");

            foreach (var file in albumFiles)
            {
                string content = File.ReadAllText(file);
                int startOfJSON = content.IndexOf("{");
                if (startOfJSON != -1)
                {
                    content = content.Substring(startOfJSON);
                    content = content.Substring(0, content.LastIndexOf("}") + 1);

                    Album? album = JsonSerializer.Deserialize<Album>(content);

                    if (album != null)
                    {
                        foreach (var picture in album.Pictures)
                        {
                            if (picture.uniqueID != null && !pictureLookup.ContainsKey(picture.uniqueID))
                            {
                                pictureLookup.Add(picture.uniqueID, picture);
                            }
                        }
                        albumList.Add(album);
                    }
                }
            }
        }

        static Dictionary<string, Product> products = new Dictionary<string, Product>();
        private static void LoadProducts()
        {
            //list products
            //get price

            var service = new ProductService();
            var options = new ProductListOptions
            {
                Expand = ["data.default_price"], 
                Limit = 100
            };

            // Synchronously paginate
            foreach (var product in service.ListAutoPaging(options))
            {
                products.Add(product.Id, product);
            }
        }

        private static List<Picture> ProcessAlbum(string subFolder, string outputPath, string baseURL)
        {

            if (!Directory.Exists(outputPath)) { Directory.CreateDirectory(outputPath); }

            List<Picture> pictures = new List<Picture>();
            var files = Directory.GetFiles(subFolder,"*.jpg");

            foreach (var file in files)
            {
                //now get the resolution of the image, get the title and caption from the EXIF
                FileInfo fileInfo = new FileInfo(file);
                long fileSize = fileInfo.Length / 1048576;

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


                int height = imageFromFile.Height;
                int width = imageFromFile.Width;

                string sourceID = $"{imageDate:u}{title}";

                sourceID = CreateHashedID(sourceID);

                //make a copy of the source image, using the unique ID
                string originalFolderPath = Path.Combine(outputPath, "originals");
                Directory.CreateDirectory(originalFolderPath);

                string outputFileName = Path.Combine(originalFolderPath, $"{sourceID}.jpg").ToLower();
                if (doFileCopyandUpload)
                {
                    File.Copy(file, outputFileName, true);
                    //ok, upload outputFileName to originals container, path = /
                    uploadToAzure(outputFileName, "originals", "");
                }


                var iptc = imageFromFile.GetIptcProfile();

                title = iptc?.GetValue(IptcTag.Title)?.Value ?? title;
                caption = iptc?.GetValue(IptcTag.Caption)?.Value ?? "";

                if (pictureLookup.ContainsKey(sourceID))
                {
                    var existingPicture = pictureLookup[sourceID];
                    title = existingPicture.Title;
                    caption = existingPicture.Caption;
                }

                var pic = new Picture()
                {
                    Caption = caption,
                    Title = title,
                    Latitude = "",
                    Longitude = "",
                    Links = new List<Link>(),
                    Camera = camera,
                    FocalLength = focalLength,
                    fStop = fValue,
                    Lens = lens,
                    DateTimeOriginal = imageDate,
                    uniqueID = sourceID
                };

                if (doFileCopyandUpload && doStripeStuff)
                {
                    List<string> thumbnails = new List<string>();
                    pic.Links.Add(ResizeAndSaveFile(outputPath, file, imageFromFile, 2160, baseURL));
                    pic.Links.Add(ResizeAndSaveFile(outputPath, file, imageFromFile, 1080, baseURL));
                    pic.Links.Add(ResizeAndSaveFile(outputPath, file, imageFromFile, 540, baseURL));
                    pic.Links.Add(ResizeAndSaveFile(outputPath, file, imageFromFile, 220, baseURL));

                    foreach (var item in pic.Links.Where(l => l.Width <= 1000))
                    {
                        thumbnails.Add(item.Url);
                    }


                    if (!notForSaleProducts.Contains(sourceID) || Math.Max(height, width) < 3000)
                    {
                        string paymentLinkURL = CreateStripeObjects(fileSize, height, width, pic.Title, pic.uniqueID, pic.Caption, thumbnails);
                        pic.PaymentLink = paymentLinkURL;
                    }
                }
                pictures.Add(pic);

            }

            return pictures;
        }

        /// <summary>
        /// Just want to create a simple string that will uniquely identify the image
        /// This is not intended to be a secret, no concerns it could be reversed
        /// </summary>
        /// <param name="sourceID">String to hash</param>
        /// <returns></returns>
        private static string CreateHashedID(string sourceID)
        {
            System.Security.Cryptography.HMACMD5 hmac = new (Encoding.ASCII.GetBytes(hashKey));

            var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes(sourceID));

            var sBuilder = new StringBuilder();

            // Loop through each byte of the hashed data
            // and format each one as a hexadecimal string.
            for (int i = 0; i < hash.Length; i++)
            {
                sBuilder.Append(hash[i].ToString("x2"));
            }

            // Return the hexadecimal string.
            sourceID = sBuilder.ToString();
            return sourceID;
        }

        /// <summary>
        /// Creates a product, price and payment link in my Stripe account for the given image.
        /// </summary>
        /// <param name="fileSize">Size of the source image in MB</param>
        /// <param name="height">Original image height in pixels</param>
        /// <param name="width">Original image width in pixels</param>
        /// <param name="pictureTitle">Taken from the EXIF data, this is the photo's title property</param>
        /// <param name="pictureId">Unique ID, to be used as the product ID, and later to fetch the source image</param>
        /// <param name="caption">If the photo has an EXIF caption, we'll include it in the product description</param>
        /// <param name="stripeThumbnail">Link to an image to be added to the product, will show on the checkout page</param>
        /// <returns>Url of the Payment Link to be used to purchase this image</returns>
        private static string CreateStripeObjects(long fileSize, int height, int width, string pictureTitle, string pictureId, string caption, List<string> stripeThumbnails)
        {

            /*
             * Given the information about this specific image, try to find an existing product or create one. 
             * Then create a price, if the product doesn't have a default price already.
             * Finally create a payment link, if there isn't one referenced in the product's metadata
             * This whole process could fail I suppose, but I don't want to just catch that and continue,
             * because I currently run this code manually on new photos, so if it fails I'll be right there to look at the 
             * error(s) and try to determine what is going wrong.
             * */

            var productService = new ProductService();

            Product? product;

            ProductGetOptions productGetOptions = new ProductGetOptions();
            productGetOptions.AddExpand("default_price");


            if (products.ContainsKey(pictureId))
            {
                product = products[pictureId];
                if (updateProducts)
                {
                    var productOptions = new ProductUpdateOptions
                    {
                        Name = pictureTitle,
                        Images = stripeThumbnails,
                        TaxCode = "txcd_10501000",
                    };
                    productService.Update(pictureId, productOptions);
                }

            }
            else
            {
                var productOptions = new ProductCreateOptions
                {
                    Name = pictureTitle,
                    Id = pictureId,
                    Images = stripeThumbnails,
                    Type = "good",
                    TaxCode = "txcd_10501000",
                };

                productOptions.AddExpand("default_price");

                string captionToAppend = "";
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    captionToAppend = $"({caption})";
                }
                productOptions.Description = $"Original digital file for this photograph, JPEG format, uncompressed and {width}px x {height}px. {fileSize}MB. Suitable for large format printing. {captionToAppend}";

                product = productService.Create(productOptions);
            }

            Price price;

            if (product.DefaultPrice != null)
            {
                price = product.DefaultPrice;
            }
            else
            {
                var priceService = new PriceService();

                var priceOptions = new PriceCreateOptions
                {
                    Nickname = "Original Image File Download",
                    Currency = "usd",
                    UnitAmount = 2000,
                    Product = product.Id
                };

                price = priceService.Create(priceOptions);

                productService.Update(product.Id, new ProductUpdateOptions { DefaultPrice = price.Id });
            }

            var paymentLinkService = new PaymentLinkService();
            PaymentLink paymentLink;

            if (product.Metadata.ContainsKey("payment_link"))
            {
                paymentLink = paymentLinkService.Get(product.Metadata["payment_link"]);
            }
            else
            {
                var paymentLinkOptions = new PaymentLinkCreateOptions
                {
                    LineItems = new List<PaymentLinkLineItemOptions>
                        {
                            new PaymentLinkLineItemOptions
                            {
                                Price = price.Id,
                                Quantity = 1,
                            },
                        },
                    AllowPromotionCodes = true
                };

                paymentLink = paymentLinkService.Create(paymentLinkOptions);
                productService.Update(product.Id, new ProductUpdateOptions { Metadata = new Dictionary<string, string> { { "payment_link", paymentLink.Id } } });
            }

            return paymentLink.Url;
        }

        /// <summary>
        /// Creates a specific resolution of hte source file
        /// </summary>
        /// <param name="outputPath">Directory for the output</param>
        /// <param name="file">Source file name</param>
        /// <param name="imageFromFile">Already created Image Magick file, no need to load the image in again</param>
        /// <param name="width">Target width in pixels</param>
        /// <param name="baseURL">Eventually base URL for the image once it is behind the CDN</param>
        /// <returns></returns>
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

            //upload newFilePath to Blob container "photos", path = baseURL - "https://photos.duncanmackenzie.net"
            uploadToAzure(newFilePath, "photos", baseURL.Replace("https://photos.duncanmackenzie.net/", ""));

            return link;
        }

        private static void uploadToAzure(string localFilePath, string containerName, string cloudPath)
        {
            BlobServiceClient serviceClient = new BlobServiceClient(azureConnectionString);
            BlobContainerClient containerClient = serviceClient.GetBlobContainerClient(containerName);

            string fileName = Path.GetFileName(localFilePath);
            string blobName = cloudPath + "/" + fileName;

            BlobClient blobClient = containerClient.GetBlobClient(blobName);

            BlobUploadOptions options = new BlobUploadOptions();

            options.HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "image/jpeg"
            };

            blobClient.Upload(path: localFilePath, options: options);
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


            if (!System.String.IsNullOrWhiteSpace(cameraMake))
            {
                if (!System.String.IsNullOrWhiteSpace(cameraModel))
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

        /// <summary>
        /// Many EXIF values come in with fractions that can be easily simplified
        /// such as 5500/100 as the focal length. This function does that work so that we 
        /// can display the cleaner value.
        /// </summary>
        /// <param name="fraction">Incoming fraction as string in the form of x/y</param>
        /// <returns></returns>
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
