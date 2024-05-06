using ImageMagick;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace GalleryMaker
{
    public class AlbumGroup
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public int Featured { get; set; }
        public List<Album> Albums { get; set; }
    }

    public class Album
    {
        public string[] Outputs { get; set; }
        public string[] Tags { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string BaseURL { get; set; }
        public int Featured {  get; set; }
        public List<Picture> Pictures { get; set; } 

    }

    public class Picture
    {
        public string Title { get; set; }
        public string uniqueID { get; set; }
        public string Caption { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        public string Camera {  get; set; }
        public string Lens { get; set; }
        public string FocalLength {  get; set; }
        public string fStop { get; set; }

        public DateTimeOffset? DateTimeOriginal { get; set; }

        public List<Link> Links { get; set; }

        public string PaymentLink { get; set; }
    }

    public class Link
    {
        public string Url { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

}
