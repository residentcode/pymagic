from django.db import migrations, models
import django.db.models.deletion


class Migration(migrations.Migration):
    initial = True
    replaces = [('shop', '0001_initial'), ('shop', '0002_book_rating')]
    dependencies = []
    operations = [
        migrations.CreateModel(
            name='Author',
            fields=[
                ('id', models.BigAutoField(auto_created=True, primary_key=True, serialize=False, verbose_name='ID')),
                ('name', models.CharField(max_length=100)),
            ],
        ),
        migrations.CreateModel(
            name='Book',
            fields=[
                ('id', models.BigAutoField(auto_created=True, primary_key=True, serialize=False, verbose_name='ID')),
                ('title', models.CharField(max_length=200)),
                ('page_count', models.IntegerField()),
                ('rating', models.DecimalField(max_digits=3, decimal_places=1, null=True)),
                ('price', models.DecimalField(max_digits=6, decimal_places=2, default=0)),
                ('author', models.ForeignKey(on_delete=django.db.models.deletion.CASCADE, to='shop.author')),
            ],
        ),
    ]
