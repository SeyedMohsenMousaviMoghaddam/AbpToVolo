using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Abp.Authorization.Users;
using Abp.Extensions;

namespace Acme.PhoneBook.Authorization.Users
{
    public class User : AbpUser<User>
    {
        public const string DefaultPassword = "123qwe";

        public const int MaxNationalCodeLength = 10;

        [MaxLength(MaxNationalCodeLength)]
        public virtual string NationalCode { get; set; }

        public virtual DateTime? BirthDate { get; set; }

        public static string CreateRandomPassword()
        {
            return Guid.NewGuid().ToString("N").Truncate(16);
        }
    }
}
