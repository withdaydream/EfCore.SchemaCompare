-- SQL script to remane a column

EXEC sp_rename 'Books.Title',  'new_name', 'COLUMN';
GO