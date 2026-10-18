import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/dashboard/dashboard.component').then(m => m.DashboardComponent)
  },
  {
    path: 'widgets',
    loadComponent: () => import('./features/widgets/layouts/widget-layout.component').then(m => m.WidgetLayoutComponent),
    children: [
      {
        path: 'selector',
        loadComponent: () => import('./features/widgets/components/widget-selector/widget-selector.component').then(m => m.WidgetSelectorComponent)
      },
      {
        path: 'group/:id',
        loadComponent: () => import('./features/widgets/components/group-widget/group-widget.component').then(m => m.GroupWidgetComponent)
      }
    ]
  }
];
