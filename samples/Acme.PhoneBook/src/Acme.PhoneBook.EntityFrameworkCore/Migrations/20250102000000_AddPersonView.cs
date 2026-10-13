using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acme.PhoneBook.Migrations
{
    public partial class AddPersonView : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // a view with a Persian comment: نمای اشخاص
            migrationBuilder.Sql(@"CREATE VIEW dbo.vPersonPhones AS
                SELECT p.Id, p.Name, COUNT(ph.Id) AS PhoneCount
                FROM PbPersons p LEFT JOIN PbPhones ph ON ph.PersonId = p.Id
                GROUP BY p.Id, p.Name");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW dbo.vPersonPhones");
        }
    }
}
