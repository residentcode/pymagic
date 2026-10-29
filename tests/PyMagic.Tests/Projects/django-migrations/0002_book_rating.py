from django.db import migrations, models


class Migration(migrations.Migration):
    dependencies = [('shop', '0001_initial')]
    operations = [
        migrations.AddField(model_name='book', name='rating', field=models.FloatField(null=True)),
        migrations.RenameField(model_name='book', old_name='pages', new_name='page_count'),
    ]
