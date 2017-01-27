var menu = new Menu();
            menu.Columns = 3;
            menu.Buttons = new List<InlineButton>
            {
                new InlineButton("Column 1, Row 1", "test"),
                new InlineButton("Column 2, Row 1", "test"),
                new InlineButton("Column 3, Row 1", "test"),
                new InlineButton("Row 2 <span across>", "test", url: "https://t.me/greywolfdev")
            };
            
            //OR
            var menu = new Menu
            {
                Columns = 3,
                Buttons = new List<InlineButton>
                {
                    new InlineButton("Column 1, Row 1", "test"),
                    new InlineButton("Column 2, Row 1", "test"),
                    new InlineButton("Column 3, Row 1", "test"),
                    new InlineButton("Row 2 <span across>", "test", url: "https://t.me/greywolfdev")
                }
            };
            
            //OR
            var menu = new Menu(3, new List<InlineButton>
                {
                    new InlineButton("Column 1, Row 1", "test"),
                    new InlineButton("Column 2, Row 1", "test"),
                    new InlineButton("Column 3, Row 1", "test"),
                    new InlineButton("Row 2 <span across>", "test", url: "https://t.me/greywolfdev")
                });
            