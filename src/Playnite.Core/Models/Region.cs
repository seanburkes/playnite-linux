using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes Region object.
    /// </summary>
    public class Region : DatabaseObject
    {
        private string specificationId;
        /// <summary>
        /// Gets or sets specification identifier.
        /// </summary>
        public string SpecificationId
        {
            get => specificationId;
            set
            {
                specificationId = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Creates new instance of <see cref="Region"/>.
        /// </summary>
        public Region(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="Region"/>.
        /// </summary>
        /// <param name="name"></param>
        public Region(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty region.
        /// </summary>
        public static readonly Region Empty = new Region(Guid.Empty, string.Empty);

        /// <inheritdoc/>
        public override void CopyDiffTo(object target)
        {
            base.CopyDiffTo(target);

            if (target is Region tro)
            {
                if (!string.Equals(SpecificationId, tro.SpecificationId, StringComparison.Ordinal))
                {
                    tro.SpecificationId = SpecificationId;
                }
            }
            else
            {
                throw new ArgumentException($"Target object has to be of type {GetType().Name}");
            }
        }
    }
}
