import React from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router-dom';
import { AppShell, Landing, LoginPage, ProfilePage, RecordPage, RegisterPage, RouteError, SignalPage } from './screens';
import './styles.css';

const router = createBrowserRouter([{
  path: '/',
  element: <AppShell />,
  errorElement: <RouteError />,
  children: [
    { index: true, element: <Landing /> },
    { path: 'register', element: <RegisterPage /> },
    { path: 'login', element: <LoginPage /> },
    { path: 'supplier/:eik', element: <ProfilePage /> },
    { path: 'supplier/:eik/signals/:signalKey', element: <SignalPage /> },
    { path: 'supplier/:eik/records/:recordId', element: <RecordPage /> }
  ]
}]);

createRoot(document.getElementById('root')!).render(
  <React.StrictMode><RouterProvider router={router} /></React.StrictMode>
);
