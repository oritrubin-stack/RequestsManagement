import { Component } from '@angular/core';
import { UserNameFieldComponent } from './core/user/user-name-field.component';
import { RequestsPageComponent } from './features/requests/requests-page.component';

@Component({
  selector: 'app-root',
  imports: [RequestsPageComponent, UserNameFieldComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent {}
